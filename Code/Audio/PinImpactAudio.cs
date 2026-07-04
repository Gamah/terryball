using Sandbox;
using System;
using System.Collections.Generic;

namespace Terryball;

/// <summary>
/// Plays the pins' impact sounds from a palette of real CC0 bowling recordings (in
/// <c>Assets/sounds/pins/</c>), jumbled per contact the way bowling games do: each pin collision
/// fires a short one-shot picked at random with pitch/volume variation, so a clean pocket strike and
/// a weak corner tap sound different purely from how the individual hits layer up — no pre-baked
/// "crash" sample. (We abandoned the procedural synth: it kept reading as metallic/hollow.) The samples
/// are dry/close-mic'd, normalized + de-harshed; the alley's room reverb is added live in-engine by
/// <see cref="AlleyReverb"/> (a DSP reverb on the Game mixer), not baked into the files.
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

	/// <summary>Minimum contact impulse for a pin/deck hit to sound. Sliding/grinding contacts report
	/// a high normal speed but ~zero impulse (no real momentum exchanged) — gating on impulse drops
	/// that spam so the actual hits punch through. Ball↔pin is exempt (always the headline hit).</summary>
	[Property] public float MinImpulse { get; set; } = 8f;

	/// <summary>Closing speed mapped to full volume. Anything faster just clamps.</summary>
	[Property] public float LoudNormalSpeed { get; set; } = 600f;

	[Property] public float Volume { get; set; } = 1.0f;

	/// <summary>Per-play pitch spread (the recordings are at one pitch; this stops repeats sounding
	/// identical). The lane is ~17k units long, so the falloff is generous on purpose.</summary>
	[Property] public Vector2 PitchRange { get; set; } = new( 0.93f, 1.04f );
	[Property] public float SoundDistance { get; set; } = 50000f;

	/// <summary>Most impacts to start in a single frame — caps a 10-pin scatter's burst. Ball↔pin
	/// hits are exempt (see ReportImpact), so this only throttles clack/deck chatter.</summary>
	[Property] public int MaxImpactsPerFrame { get; set; } = 16;

	// Diagnostic logging of every contact + play/skip decision, on its OWN toggle
	// (GameDirector.LogPinAudio) so it isn't drowned by the per-frame physics [coast] spam.
	private GameDirector _director;
	// This lane's director — the networked object we broadcast impact sounds through (and the debug toggle).
	private GameDirector Director => _director.IsValid() ? _director : (_director = Lane.Of( this )?.Director);
	public bool Debug => Director?.LogPinAudio ?? false;

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

	// EXACT counts of the shipped clackN.wav / thudN.wav files. Must match what's in
	// Assets/sounds/pins/ — SoundFile.Load returns a BROKEN (non-null) handle for a missing file
	// rather than null, so probing past the real count fills the random pool with silent picks.
	[Property] public int ClackVariants { get; set; } = 6;
	[Property] public int ThudVariants { get; set; } = 2;

	protected override void OnEnabled()
	{
		_clack = BuildEvent( "clack", ClackVariants );
		_thud = BuildEvent( "thud", ThudVariants );
		_ready = _clack != null && _thud != null;
		if ( !_ready )
			Log.Warning( "[terryball] PinImpactAudio: no pin sounds loaded from Assets/sounds/pins/ — pins will be silent." );
	}

	// Load EXACTLY count files (sounds/pins/<prefix>0.wav .. <prefix>{count-1}.wav) into a SoundEvent
	// that picks one at random with a little pitch spread per play. Don't over-probe: a missing index
	// loads as a broken handle that plays silence.
	private SoundEvent BuildEvent( string prefix, int count )
	{
		var sounds = new List<SoundFile>();
		for ( int i = 0; i < count; i++ )
		{
			var sf = SoundFile.Load( $"sounds/pins/{prefix}{i}.wav" );
			if ( sf != null ) sounds.Add( sf );
		}
		if ( sounds.Count == 0 ) return null;

		return new SoundEvent
		{
			Sounds = sounds,
			Volume = 1.0f, // actual level is set per-hit from impact strength
			Pitch = new RangedFloat( PitchRange.x, PitchRange.y ),
		};
	}

	/// <summary>What a pin struck — the three read very differently.</summary>
	public enum ContactKind { Pin, Ball, Structure }

	/// <summary>Funnelled here by a pin's listener. <see cref="ContactKind"/> picks the voicing:
	/// pin↔pin = wood clack; ball↔pin = the pocket smash (prominent clack + low boom); pin↔deck/
	/// backstop = low thud.</summary>
	public void ReportImpact( Vector3 worldPos, float normalSpeed, float impulse, ContactKind kind )
	{
		if ( !_ready ) return;

		// Muted right after a rack/respot: the pins were just teleported onto their spots and their settling
		// contacts would clack while the ball resets. Skip entirely (so nothing broadcasts either).
		if ( Director?.PinAudioQuietUntil > Time.Now )
		{
			if ( Debug ) Log.Info( $"[pinaudio] skip {kind} — racking (muted)" );
			return;
		}

		if ( normalSpeed < MinNormalSpeed )
		{
			if ( Debug ) Log.Info( $"[pinaudio] skip {kind} normalSpeed={normalSpeed:F0} < min {MinNormalSpeed}" );
			return;
		}
		// drop sliding/grinding contacts (high relative speed, ~no impulse); the ball hit is exempt
		if ( kind != ContactKind.Ball && impulse < MinImpulse )
		{
			if ( Debug ) Log.Info( $"[pinaudio] skip {kind} impulse={impulse:F0} < min {MinImpulse} (grind/slide)" );
			return;
		}

		// reset the per-frame budget when a new frame starts
		if ( Time.Now != _lastFrameTime )
		{
			_lastFrameTime = Time.Now;
			_playsThisFrame = 0;
		}

		// The ball striking the pins is THE moment of the throw — never let the burst cap or coalesce
		// cull it. Only pin↔pin and pin↔deck chatter is subject to those.
		bool important = kind == ContactKind.Ball;
		if ( !important )
		{
			if ( _playsThisFrame >= MaxImpactsPerFrame )
			{
				if ( Debug ) Log.Info( $"[pinaudio] skip {kind} — frame cap {MaxImpactsPerFrame}" );
				return;
			}
			if ( IsCoalesced( worldPos ) )
			{
				if ( Debug ) Log.Info( $"[pinaudio] skip {kind} — coalesced" );
				return;
			}
		}

		float strength = Math.Clamp(
			(normalSpeed - MinNormalSpeed) / Math.Max( 1f, LoudNormalSpeed - MinNormalSpeed ), 0f, 1f );
		// ease so soft taps aren't silent and hard hits don't all pin to full
		float amplitude = MathF.Sqrt( strength ) * Volume;

		if ( Debug ) Log.Info( $"[pinaudio] PLAY {kind} normalSpeed={normalSpeed:F0} amp={amplitude:F2}" );

		// The physics only run on the machine that owns the rack (the bowler), so a bare local Sound.Play
		// would only be heard by them. Route the final play decision through the lane's director as a
		// networked broadcast so EVERY client (spectators + adjacent lanes) hears the crash. Editor/offline
		// (no live session): play it straight here.
		if ( Networking.IsActive && Director.IsValid() )
			Director.NetPinImpact( worldPos, (int)kind, amplitude );
		else
			PlayRaw( worldPos, kind, amplitude );

		_playsThisFrame++;
		_recent.Add( (Time.Now, worldPos) );
	}

	/// <summary>Actually voice an impact locally (positional). Called on every client from the director's
	/// <see cref="GameDirector.NetPinImpact"/> broadcast, so spectators and adjacent lanes hear it too.
	/// The <see cref="ContactKind"/> picks the voicing: pin↔pin = wood clack; ball↔pin = pocket smash
	/// (clack + low boom); pin↔deck/backstop = low thud.</summary>
	public void PlayRaw( Vector3 worldPos, ContactKind kind, float amplitude )
	{
		if ( !_ready ) return;
		switch ( kind )
		{
			case ContactKind.Ball:
				// pocket smash: the wood cracks AND the low body booms; bias loud, it's the headline hit
				Play( _clack, worldPos, MathF.Max( 0.6f, amplitude ) );
				Play( _thud, worldPos, MathF.Max( 0.5f, amplitude ) );
				break;
			case ContactKind.Structure:
				Play( _thud, worldPos, amplitude );
				if ( amplitude > 0.6f * Volume ) Play( _clack, worldPos, amplitude * 0.6f ); // hard deck slam cracks too
				break;
			default: // Pin
				Play( _clack, worldPos, amplitude );
				break;
		}
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

		var other = c.Other.GameObject;
		PinImpactAudio.ContactKind kind;
		if ( other.IsValid() && other.Components.Get<PinImpactListener>() is not null )
			kind = PinImpactAudio.ContactKind.Pin;
		else if ( other.IsValid() && other.Components.Get<BallController>() is not null )
			kind = PinImpactAudio.ContactKind.Ball; // the ball striking the rack — the headline hit
		else
			kind = PinImpactAudio.ContactKind.Structure; // deck, backstop, gutter, pit

		if ( Bank.Debug )
			Log.Info( $"[pinaudio] contact {GameObject.Name} <- {kind} ({other?.Name}) " +
				$"normalSpeed={c.Contact.NormalSpeed:F0} impulse={c.Contact.Impulse:F0}" );

		// The ball hit is never gated: it's the moment of the throw. Pin/deck chatter gets a cooldown
		// so one jostle doesn't re-trigger every physics step.
		if ( kind != PinImpactAudio.ContactKind.Ball )
		{
			if ( Time.Now < _nextAllowed ) return;
			_nextAllowed = Time.Now + Cooldown;
		}

		// Pin-on-pin fires this callback on BOTH pins for the one contact. We deliberately DON'T pick a
		// single side — if that side were on cooldown the clack would be dropped; instead both report
		// and the bank coalesces the duplicate by position + time.
		Bank.ReportImpact( c.Contact.Point, c.Contact.NormalSpeed, c.Contact.Impulse, kind );
	}
}
