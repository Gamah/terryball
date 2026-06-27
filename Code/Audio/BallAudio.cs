using Sandbox;
using System;
using System.Collections.Generic;

namespace Terryball;

/// <summary>
/// The ball's own audio, on the ball GameObject:
///   • a one-shot THROW sound when the ball crosses the foul line (lane entry), jumbled from a small
///     CC0 sample set with pitch/volume RNG so it never sounds identical;
///   • a continuous ROLL rumble whose level/brightness tracks ball speed, with extra "rumble" while
///     the player is steering (turning) on the approach.
///
/// The roll is synthesised (filtered noise fed through a <see cref="SoundStream"/>) rather than a
/// looped sample: it's inherently seamless and modulates in real time on speed + steering, which a
/// looped clip can't do cleanly — and a rolling rumble is just broadband noise, so there's no
/// "metallic" risk like the pitched pin hits had. The throw, being a one-shot, stays sample-based.
/// </summary>
public sealed class BallAudio : Component
{
	[Property] public float Volume { get; set; } = 1.0f;
	[Property] public float SoundDistance { get; set; } = 50000f;

	[Property, Group( "Throw" )] public Vector2 ThrowPitchRange { get; set; } = new( 0.92f, 1.08f );
	[Property, Group( "Throw" )] public int ThrowVariants { get; set; } = 3;

	/// <summary>Speed (units/s) treated as "full" roll — speed maps roughly linearly to this.</summary>
	[Property, Group( "Roll" )] public float RollSpeedRef { get; set; } = 3500f;
	[Property, Group( "Roll" )] public float RollVolume { get; set; } = 0.7f;
	/// <summary>How much steering effort adds rumble (amplitude wobble) on top of the base roll.</summary>
	[Property, Group( "Roll" )] public float SteerRumble { get; set; } = 0.8f;

	private const int SR = 44100;

	private BallController _ball;
	private LaneBuilder _lane;
	private SoundEvent _throw;

	// throw-crossing state
	private float _prevX;
	private bool _thrown;
	private bool _havePrev;

	// roll rumble
	private SoundStream _roll;
	private SoundHandle _rollHandle;
	private short[] _rollBuf;
	private float _lp1, _lp2;     // two-pole lowpass state (deep rumble)
	private float _amSmoothed = 1f;
	private float _amPhase;
	private uint _rng = 0x1234567u;

	protected override void OnEnabled()
	{
		_ball = Components.Get<BallController>();
		_lane = Lane.Of( this )?.Builder;
		_throw = BuildThrow();

		_rollBuf = new short[(int)(0.2f * SR)];
		_roll = new SoundStream( SR, 1 );
		_rollHandle = _roll.Play( 1f ); // handle at unity; actual loudness rides on the PCM amplitude
		if ( _rollHandle.IsValid() )
		{
			_rollHandle.SpacialBlend = 1f;
			_rollHandle.Distance = SoundDistance;
		}
	}

	protected override void OnDisabled()
	{
		_roll?.Dispose();
		_roll = null;
	}

	private SoundEvent BuildThrow()
	{
		var sounds = new List<SoundFile>();
		for ( int i = 0; i < ThrowVariants; i++ )
		{
			var sf = SoundFile.Load( $"sounds/ball/throw{i}.wav" );
			if ( sf != null ) sounds.Add( sf );
		}
		if ( sounds.Count == 0 ) return null;
		return new SoundEvent
		{
			Sounds = sounds,
			Volume = 1.0f,
			Pitch = new RangedFloat( ThrowPitchRange.x, ThrowPitchRange.y ),
		};
	}

	protected override void OnUpdate()
	{
		if ( !_ball.IsValid() ) return;
		var pos = WorldPosition;

		ThrowCheck( pos );
		FeedRoll( pos );
	}

	// Fire the throw once when the ball crosses the foul line going down-lane; re-arm when it returns
	// behind the line (next rack / respot).
	private void ThrowCheck( Vector3 pos )
	{
		if ( _throw == null || _lane == null ) return;
		float foul = _lane.FoulLineWorldX;
		float x = pos.x;

		if ( _havePrev )
		{
			if ( !_thrown && _prevX < foul && x >= foul )
			{
				_thrown = true;
				var h = Sound.Play( _throw, pos );
				if ( h.IsValid() ) { h.Volume = Volume; h.Distance = SoundDistance; }
			}
			else if ( _thrown && x < foul - 50f )
			{
				_thrown = false; // ball is back on the approach — arm the next throw
			}
		}
		_prevX = x;
		_havePrev = true;
	}

	// Keep the rumble stream topped up with filtered noise whose amplitude/brightness follow speed,
	// plus a steering-driven amplitude wobble that reads as a rumble while the player adjusts.
	private void FeedRoll( Vector3 pos )
	{
		if ( _roll == null || !_rollHandle.IsValid() ) return;

		_rollHandle.Position = pos;

		float speed01 = (_ball.Velocity.Length / Math.Max( 1f, RollSpeedRef )).Clamp( 0f, 1f );
		float steer01 = _ball.SteerEffort01;

		int target = (int)(0.12f * SR);                 // keep ~120ms queued
		int want = Math.Min( target - _roll.QueuedSampleCount, _rollBuf.Length );
		want = Math.Min( want, _roll.MaxWriteSampleCount );
		if ( want <= 0 ) return;

		// Brightness of the rumble rises a little with speed; deep when slow.
		float cutoff = 0.018f + speed01 * 0.06f;
		float amp = speed01 * RollVolume * Volume;
		float amInc = (28f + steer01 * 30f) / SR;       // wobble rate, faster while steering

		for ( int n = 0; n < want; n++ )
		{
			float white = NextFloat() * 2f - 1f;
			_lp1 += cutoff * (white - _lp1);
			_lp2 += cutoff * (_lp1 - _lp2);             // two-pole → deep rumble

			_amPhase += amInc; if ( _amPhase >= 1f ) _amPhase -= 1f;
			float wobble = 1f + steer01 * SteerRumble * MathF.Sin( _amPhase * MathF.PI * 2f );

			float s = _lp2 * 3.5f * amp * wobble;        // *3.5: the double-LP loses a lot of level
			int v = (int)(MathF.Tanh( s ) * 30000f);
			_rollBuf[n] = (short)Math.Clamp( v, short.MinValue, short.MaxValue );
		}

		_roll.WriteData( _rollBuf.AsSpan( 0, want ) );
	}

	private float NextFloat()
	{
		_rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
		return (_rng & 0xFFFFFF) / (float)0x1000000;
	}
}
