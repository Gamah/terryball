using Sandbox;
using System;
using System.Collections.Generic;

namespace Terryball;

/// <summary>
/// Plays the pins' procedural impact sounds. One of these lives on the lane; every pin carries a
/// <see cref="PinImpactListener"/> that funnels its collisions here. This component owns the
/// playback plumbing — stream pool, per-frame throttle, and spatial/temporal coalescing — none of
/// which knows or cares how the audio was made.
///
/// The sound itself comes from <see cref="PinSynth"/>. Two ways to wire that up, selected by
/// <see cref="Mode"/>:
///   • <b>Live</b> (this branch's default, option #1) — synthesise each impact on demand into a
///     single shared scratch buffer. Cheap (sub-ms) and reactive: amplitude and pitch track the
///     real impact strength, so every clack differs.
///   • <b>Prebaked</b> (option #2) — render a small fixed bank of variations once when the
///     component enables, then just pick one per hit. Zero per-hit DSP; the runtime profile is
///     identical to shipping WAVs. Kept working here so switching is a one-property flip, and to
///     prove the <see cref="PinSynth.Render"/> seam is genuinely shared.
/// </summary>
public sealed class PinImpactAudio : Component
{
	public enum SourceMode { Live, Prebaked }

	[Property] public SourceMode Mode { get; set; } = SourceMode.Live;

	/// <summary>Closing speed (units/s) below which an impact is inaudible chatter and skipped.</summary>
	[Property] public float MinNormalSpeed { get; set; } = 40f;

	/// <summary>Closing speed mapped to full volume. Anything faster just clamps.</summary>
	[Property] public float LoudNormalSpeed { get; set; } = 600f;

	[Property] public float Volume { get; set; } = 1.0f;

	/// <summary>Falloff distance for the spatialised one-shots. The lane is ~17k units long at the
	/// default scale (~23 units/inch), so this is generous on purpose — short impulsive sounds gain
	/// nothing from a tight radius and everything from not being silently culled.</summary>
	[Property] public float SoundDistance { get; set; } = 50000f;

	/// <summary>Most impacts to start in a single frame — caps a 10-pin scatter's burst.</summary>
	[Property] public int MaxImpactsPerFrame { get; set; } = 8;

	/// <summary>Most one-shots sounding at once (each ~1s with the room tail); oldest stolen past this.</summary>
	[Property] public int MaxConcurrent { get; set; } = 16;

	/// <summary>A new impact within this time AND <see cref="CoalesceRadius"/> of a recent one is
	/// dropped — the many micro-contacts of a scatter collapse into distinct clacks, not a buzz.</summary>
	[Property] public float CoalesceWindow { get; set; } = 0.035f;
	[Property] public float CoalesceRadius { get; set; } = 60f;

	// Bake variation knobs (Prebaked mode only).
	[Property] public int PrebakedVariations { get; set; } = 8;

	/// <summary>Bake an alley room tail into each clack. The dry synth alone is bare/hollow; a room
	/// gives it the "crashing in a big hall" body. Independent of any scene geometry.</summary>
	[Property, Group( "Reverb" )] public bool EnableReverb { get; set; } = true;
	[Property, Group( "Reverb" ), Range( 0f, 0.97f )] public float ReverbRoomSize { get; set; } = 0.88f;
	[Property, Group( "Reverb" ), Range( 0f, 1f )] public float ReverbDamping { get; set; } = 0.35f;
	[Property, Group( "Reverb" ), Range( 0f, 1f )] public float ReverbWet { get; set; } = 0.32f;
	[Property, Group( "Reverb" )] public float ReverbTailSeconds { get; set; } = 0.7f;

	// One scratch buffer feeds every Live impact: WriteData copies into the native ring synchronously,
	// so we can refill and reuse it immediately. _dry holds the raw synth; _wet holds it plus the room
	// tail (longer, so it needs more room).
	private const float MaxDrySeconds = 0.35f;
	private short[] _dry;
	private short[] _wet;
	private PinReverb _reverb;

	// Prebaked banks: a handful of pre-rendered clacks per material, picked at random per hit.
	private List<(short[] buf, int len)> _bakedPin;
	private List<(short[] buf, int len)> _bakedStructure;

	private readonly List<Active> _active = new();
	private readonly List<(float time, Vector3 pos)> _recent = new();
	private int _playsThisFrame;
	private float _lastFrameTime;

	private struct Active { public SoundStream Stream; public SoundHandle Handle; }

	protected override void OnEnabled()
	{
		int dryMax = (int)(MaxDrySeconds * PinSynth.SampleRate);
		int wetMax = dryMax + (int)((ReverbTailSeconds + 0.1f) * PinSynth.SampleRate);
		_dry = new short[dryMax];
		_wet = new short[wetMax];
		_reverb = new PinReverb();

		if ( Mode == SourceMode.Prebaked )
		{
			_bakedPin = Bake( PinSynth.PinOnPin, wetMax );
			_bakedStructure = Bake( PinSynth.PinOnStructure, wetMax );
		}
	}

	private PinReverb.Settings ReverbSettings => new()
	{
		RoomSize = ReverbRoomSize,
		Damping = ReverbDamping,
		Wet = ReverbWet,
		TailSeconds = ReverbTailSeconds,
	};

	protected override void OnDisabled()
	{
		foreach ( var a in _active ) a.Stream?.Dispose();
		_active.Clear();
		_recent.Clear();
	}

	private List<(short[] buf, int len)> Bake( in PinSynth.Recipe recipe, int wetMax )
	{
		// The SAME render (and reverb) the live path uses — just run once, into kept buffers.
		var list = new List<(short[], int)>( PrebakedVariations );
		for ( int i = 0; i < PrebakedVariations; i++ )
		{
			var buf = new short[wetMax];
			float pitch = 0.9f + i / (float)Math.Max( 1, PrebakedVariations - 1 ) * 0.25f;
			int dryLen = PinSynth.Render( recipe, 1.0f, pitch, seed: i * 374761393 + 17, _dry );
			int len = WetLength( dryLen, buf );
			list.Add( (buf, len) );
		}
		return list;
	}

	// Run the dry render in _dry through the reverb into dest, or just copy it dry. Returns length.
	private int WetLength( int dryLen, short[] dest )
	{
		if ( !EnableReverb || ReverbWet <= 0f )
		{
			int n = Math.Min( dryLen, dest.Length );
			Array.Copy( _dry, dest, n );
			return n;
		}
		return _reverb.Process( _dry, dryLen, ReverbSettings, dest );
	}

	/// <summary>Funnelled here by a pin's listener. <paramref name="onStructure"/> picks the duller
	/// voice (pin hit the lane/deck), otherwise it's the wood-on-wood clack.</summary>
	public void ReportImpact( Vector3 worldPos, float normalSpeed, bool onStructure, int seed )
	{
		if ( normalSpeed < MinNormalSpeed ) return;

		// reset the per-frame budget when a new frame starts
		if ( Time.Now != _lastFrameTime )
		{
			_lastFrameTime = Time.Now;
			_playsThisFrame = 0;
		}
		if ( _playsThisFrame >= MaxImpactsPerFrame ) return;
		if ( IsCoalesced( worldPos ) ) return;

		float strength = MathX.Clamp(
			(normalSpeed - MinNormalSpeed) / Math.Max( 1f, LoudNormalSpeed - MinNormalSpeed ), 0f, 1f );
		// perceptual: ease volume so soft taps aren't silent and hard hits don't all pin to 1
		float amplitude = MathF.Sqrt( strength ) * Volume;
		// harder hits a touch brighter; plus a little per-hit pitch scatter
		float pitch = 0.95f + strength * 0.2f;

		if ( Mode == SourceMode.Live )
		{
			var recipe = onStructure ? PinSynth.PinOnStructure : PinSynth.PinOnPin;
			int dryLen = PinSynth.Render( recipe, amplitude, pitch, seed, _dry );
			int len = WetLength( dryLen, _wet );
			PlayPcm( worldPos, _wet, len, 1f, 1f );
		}
		else
		{
			var bank = onStructure ? _bakedStructure : _bakedPin;
			if ( bank == null || bank.Count == 0 ) return;
			var pick = bank[Math.Abs( seed ) % bank.Count];
			PlayPcm( worldPos, pick.buf, pick.len, amplitude, pitch );
		}

		_playsThisFrame++;
		_recent.Add( (Time.Now, worldPos) );
	}

	private bool IsCoalesced( Vector3 pos )
	{
		float r2 = CoalesceRadius * CoalesceRadius;
		foreach ( var (t, p) in _recent )
		{
			if ( Time.Now - t <= CoalesceWindow && (p - pos).LengthSquared <= r2 )
				return true;
		}
		return false;
	}

	private void PlayPcm( Vector3 pos, short[] buf, int len, float volume, float pitch )
	{
		if ( len <= 0 || buf == null ) return;

		if ( _active.Count >= MaxConcurrent )
		{
			_active[0].Stream?.Dispose();
			_active.RemoveAt( 0 );
		}

		var stream = new SoundStream( PinSynth.SampleRate, 1 );
		stream.WriteData( buf.AsSpan( 0, len ) ); // queue the samples BEFORE the voice starts, so a
		                                          // one-shot can't drain-and-finish before they land

		var handle = stream.Play( volume, pitch );
		if ( !handle.IsValid() )
		{
			stream.Dispose();
			return;
		}

		handle.Position = pos;
		handle.SpacialBlend = 1f;
		handle.Distance = SoundDistance;

		stream.Close(); // no more data — drains the queued buffer then stops

		_active.Add( new Active { Stream = stream, Handle = handle } );
	}

	protected override void OnUpdate()
	{
		// reap finished one-shots
		for ( int i = _active.Count - 1; i >= 0; i-- )
		{
			var h = _active[i].Handle;
			if ( !h.IsValid() || !h.IsPlaying )
			{
				_active[i].Stream?.Dispose();
				_active.RemoveAt( i );
			}
		}

		// prune the coalesce window
		for ( int i = _recent.Count - 1; i >= 0; i-- )
		{
			if ( Time.Now - _recent[i].time > CoalesceWindow )
				_recent.RemoveAt( i );
		}
	}
}

/// <summary>
/// Rides on every pin. When the pin starts touching something hard enough, it classifies the other
/// surface and hands the impact to the lane's <see cref="PinImpactAudio"/>. Pure routing — it makes
/// no sound itself.
/// </summary>
public sealed class PinImpactListener : Component, Component.ICollisionListener
{
	/// <summary>The lane's audio bank. Wired up by <c>LaneBuilder.MakePin</c>.</summary>
	public PinImpactAudio Bank { get; set; }

	// Stop a single jostle from re-triggering every physics step.
	[Property] public float Cooldown { get; set; } = 0.04f;
	private float _nextAllowed;

	public void OnCollisionStart( Collision c )
	{
		if ( !Bank.IsValid() ) return;
		if ( Time.Now < _nextAllowed ) return;

		var other = c.Other.GameObject;
		bool otherIsPin = other.IsValid() && other.Components.Get<PinImpactListener>() is not null;

		// Pin-on-pin fires this callback on BOTH pins for the one contact. Let only one side voice it
		// (deterministic by id) so we don't double-clack.
		if ( otherIsPin && GameObject.GetHashCode() > other.GetHashCode() )
			return;

		_nextAllowed = Time.Now + Cooldown;
		Bank.ReportImpact( c.Contact.Point, c.Contact.NormalSpeed, onStructure: !otherIsPin, seed: GetSeed( c ) );
	}

	private int _hits;

	private int GetSeed( Collision c )
	{
		// Vary per pin and per contact so successive hits don't render identically.
		return GameObject.GetHashCode() ^ (int)(c.Contact.NormalSpeed * 7.3f) ^ (_hits++ * 374761393);
	}
}
