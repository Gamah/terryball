using Sandbox;
using System;
using System.Collections.Generic;

namespace Terryball;

/// <summary>
/// Plays the pins' impact sounds from a palette of real CC0 bowling recordings (in
/// <c>Assets/sounds/pins/</c>), jumbled per contact the way bowling games do: each pin collision
/// fires a short one-shot picked at random with pitch/volume variation, so a clean pocket strike and
/// a weak corner tap sound different purely from how the individual hits layer up — no pre-baked
/// "crash" sample. (We abandoned the procedural synth: it kept reading as metallic/hollow, and real
/// recordings carry their own room ambience, which is our reverb.)
///
/// One of these lives on the lane; every pin carries a <see cref="PinImpactListener"/> that funnels
/// its collisions here. This component owns classification (wood clack vs low thud), the per-frame
/// burst cap, and spatial/temporal coalescing; the random pick + pitch/volume spread is handled by
/// s&amp;box's <see cref="SoundEvent"/>.
/// </summary>
public sealed class PinImpactAudio : Component
{
	/// <summary>Closing speed (units/s) below which an impact is inaudible chatter and skipped. Low,
	/// so gentle 1-2 pin taps still clack — only true settling jitter falls under it.</summary>
	[Property] public float MinNormalSpeed { get; set; } = 12f;

	/// <summary>Closing speed mapped to full volume. Anything faster just clamps.</summary>
	[Property] public float LoudNormalSpeed { get; set; } = 600f;

	[Property] public float Volume { get; set; } = 1.0f;

	/// <summary>Per-play pitch spread (the recordings are at one pitch; this stops repeats sounding
	/// identical). The lane is ~17k units long, so the falloff is generous on purpose.</summary>
	[Property] public Vector2 PitchRange { get; set; } = new( 0.82f, 0.94f );
	[Property] public float SoundDistance { get; set; } = 50000f;

	/// <summary>Most impacts to start in a single frame — caps a 10-pin scatter's burst.</summary>
	[Property] public int MaxImpactsPerFrame { get; set; } = 10;

	/// <summary>A new impact within this time AND <see cref="CoalesceRadius"/> of a recent one is
	/// dropped — the many micro-contacts of a scatter collapse into distinct clacks, not a buzz.</summary>
	[Property] public float CoalesceWindow { get; set; } = 0.03f;
	[Property] public float CoalesceRadius { get; set; } = 55f;

	private SoundEvent _clack; // pin-on-pin wood hit
	private SoundEvent _thud;  // pin/ball against deck, backstop, pit — the low whump
	private bool _ready;

	private readonly List<(float time, Vector3 pos)> _recent = new();
	private int _playsThisFrame;
	private float _lastFrameTime;

	protected override void OnEnabled()
	{
		_clack = BuildEvent( "clack", 16 );
		_thud = BuildEvent( "thud", 8 );
		_ready = _clack != null && _thud != null;
		if ( !_ready )
			Log.Warning( "[terryball] PinImpactAudio: no pin sounds loaded from Assets/sounds/pins/ — pins will be silent." );
	}

	// Load sounds/pins/<prefix>0.wav, <prefix>1.wav, ... until one is missing, and wrap them in a
	// SoundEvent that picks one at random with a little pitch spread per play.
	private SoundEvent BuildEvent( string prefix, int max )
	{
		var sounds = new List<SoundFile>();
		for ( int i = 0; i < max; i++ )
		{
			var sf = SoundFile.Load( $"sounds/pins/{prefix}{i}.wav" );
			if ( sf == null ) break;
			sounds.Add( sf );
		}
		if ( sounds.Count == 0 ) return null;

		return new SoundEvent
		{
			Sounds = sounds,
			Volume = 1.0f, // actual level is set per-hit from impact strength
			Pitch = new RangedFloat( PitchRange.x, PitchRange.y ),
		};
	}

	/// <summary>Funnelled here by a pin's listener. <paramref name="onStructure"/> is true when the
	/// pin hit something that isn't another pin (the ball, the deck, the backstop, the pit) — that's
	/// the low thud; pin-on-pin is the wood clack.</summary>
	public void ReportImpact( Vector3 worldPos, float normalSpeed, bool onStructure )
	{
		if ( !_ready ) return;
		if ( normalSpeed < MinNormalSpeed ) return;

		// reset the per-frame budget when a new frame starts
		if ( Time.Now != _lastFrameTime )
		{
			_lastFrameTime = Time.Now;
			_playsThisFrame = 0;
		}
		if ( _playsThisFrame >= MaxImpactsPerFrame ) return;
		if ( IsCoalesced( worldPos ) ) return;

		float strength = Math.Clamp(
			(normalSpeed - MinNormalSpeed) / Math.Max( 1f, LoudNormalSpeed - MinNormalSpeed ), 0f, 1f );
		// ease so soft taps aren't silent and hard hits don't all pin to full
		float amplitude = MathF.Sqrt( strength ) * Volume;

		if ( onStructure )
		{
			Play( _thud, worldPos, amplitude );
			// a hard ball-into-pocket cracks as well as booms — layer a clack on top
			if ( strength > 0.5f ) Play( _clack, worldPos, amplitude * 0.7f );
		}
		else
		{
			Play( _clack, worldPos, amplitude );
		}

		_playsThisFrame++;
		_recent.Add( (Time.Now, worldPos) );
	}

	private void Play( SoundEvent ev, Vector3 pos, float volume )
	{
		var h = Sound.Play( ev, pos );
		if ( !h.IsValid() ) return;
		h.Volume = volume;
		h.Distance = SoundDistance;
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

	protected override void OnUpdate()
	{
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

		// Pin-on-pin fires this callback on BOTH pins for the one contact. We deliberately DON'T pick a
		// single side here — if that side were on cooldown (e.g. the ball-struck pin) the clack would
		// be dropped entirely. Instead both report, and the bank coalesces the duplicate by position +
		// time, so whichever side isn't on cooldown still voices it.
		_nextAllowed = Time.Now + Cooldown;
		Bank.ReportImpact( c.Contact.Point, c.Contact.NormalSpeed, onStructure: !otherIsPin );
	}
}
