using Sandbox;
using System;
using System.Collections.Generic;

namespace Terryball;

/// <summary>
/// The ball's own audio, on the ball GameObject:
///   • a one-shot THROW sound when the ball crosses the foul line (lane entry), jumbled from a small
///     CC0 sample set with pitch/volume RNG so it never sounds identical;
///   • a continuous ROLL rumble whose level/brightness tracks ball speed, with a per-revolution
///     "whump whump whump" pulse (spin rate derived from speed) plus extra "rumble" while the
///     player is steering (turning) on the approach.
///
/// The roll is synthesised (filtered noise fed through a <see cref="SoundStream"/>) rather than a
/// looped sample: it's inherently seamless and modulates in real time on speed + steering, which a
/// looped clip can't do cleanly. The throw, being a one-shot, is sample-based.
/// </summary>
public sealed class BallAudio : Component
{
	[Property] public float Volume { get; set; } = 1.0f;
	[Property] public float SoundDistance { get; set; } = 50000f;

	[Property, Group( "Throw" )] public Vector2 ThrowPitchRange { get; set; } = new( 0.9f, 1.1f );
	[Property, Group( "Throw" )] public Vector2 ThrowVolumeRange { get; set; } = new( 0.85f, 1.0f );
	// One clean ball-only throw clip, varied by pitch/volume RNG. (Don't add more clips unless they're
	// pin-free — random selection across recordings that contain pin strikes plays pins on the throw.)
	[Property, Group( "Throw" )] public int ThrowVariants { get; set; } = 1;

	/// <summary>Speed (units/s) treated as "full" roll — speed maps roughly linearly to this.</summary>
	[Property, Group( "Roll" )] public float RollSpeedRef { get; set; } = 4500f;
	[Property, Group( "Roll" )] public float RollVolume { get; set; } = 0.3f;
	/// <summary>How much steering effort adds rumble (amplitude wobble) on top of the base roll.</summary>
	[Property, Group( "Roll" )] public float SteerRumble { get; set; } = 0.5f;

	/// <summary>"whump whump whump" depth — how hard the per-revolution pulse swells the roll (0 = off).</summary>
	[Property, Group( "Roll" )] public float RollWhump { get; set; } = 0.8f;
	/// <summary>
	/// Whumps per second at full speed (<see cref="RollSpeedRef"/>) — the ball's apparent spin rate.
	/// Real bowling rev rate is ~300-360 RPM (≈5-6 rev/s); high-rev pros reach ~450 RPM (~7.5 Hz). At
	/// full speed we sit near the top of the typical range; below full speed the whumps space out.
	/// </summary>
	[Property, Group( "Roll" )] public float RollWhumpRate { get; set; } = 5.5f;
	/// <summary>Seconds for the whump rate to spin up from 0 (at the throw) to match speed — a normal
	/// shot runs the lane in ~2.5s, so the ball is "revving up" for most of that travel.</summary>
	[Property, Group( "Roll" )] public float RollSpinUpTime { get; set; } = 2.5f;
	/// <summary>Seconds for the whump rate to fall to match a drop in speed — aggressive, so a stall
	/// or slowdown decelerates the whumps far faster than they spun up.</summary>
	[Property, Group( "Roll" )] public float RollSpinDownTime { get; set; } = 0.4f;

	private const int SR = 44100;

	private BallController _ball;
	private LaneBuilder _lane;
	private SoundEvent _throw;

	// throw-crossing state
	private float _prevX;
	private bool _thrown;
	private bool _havePrev;

	// world-position-derived speed (proxy-safe; see OnUpdate)
	private Vector3 _lastPos;
	private bool _havePos;
	private float _worldSpeed;

	// roll rumble
	private SoundStream _roll;
	private SoundHandle _rollHandle;
	private short[] _rollBuf;
	private float _lp1, _lp2;     // two-pole lowpass state (deep rumble)
	private float _amPhase;
	private float _whumpPhase;     // per-revolution "whump" cycle, advanced by spin rate
	private float _spinRate01;     // ramped spin: 0 at throw, climbs to match speed, falls fast on slowdown
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

		// Ball SPEED from world-position delta, not Rigidbody.Velocity — position replicates to every client
		// but a proxy's rigidbody velocity may read ~0, which would silence the roll for spectators (and
		// adjacent lanes). Deriving it from the transform means everyone hears the same roll.
		if ( _havePos && Time.Delta > 0f )
			_worldSpeed = (pos - _lastPos).Length / Time.Delta;
		_lastPos = pos;
		_havePos = true;

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
				var h = Sound.Play( _throw, pos ); // pitch RNG comes from the SoundEvent
				if ( h.IsValid() )
				{
					h.Volume = Volume * (ThrowVolumeRange.x + NextFloat() * (ThrowVolumeRange.y - ThrowVolumeRange.x));
					h.Distance = SoundDistance;
				}
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

		int target = (int)(0.12f * SR);                 // keep ~120ms queued
		int want = Math.Min( target - _roll.QueuedSampleCount, _rollBuf.Length );
		want = Math.Min( want, _roll.MaxWriteSampleCount );
		if ( want <= 0 ) return;

		// The roll only sounds AFTER the ball crosses the foul line (the throw), until it resets back
		// behind the line. Before the throw — on the approach — it stays silent.
		float speed01 = _thrown ? (_worldSpeed / Math.Max( 1f, RollSpeedRef )).Clamp( 0f, 1f ) : 0f;

		// Whump RATE doesn't follow speed directly: it ramps from 0 at the throw up to the speed target
		// over a normal shot (the ball revving up off the release), then drops fast if the ball slows.
		float spinTarget = _thrown ? speed01 : 0f;
		float spinSecs = spinTarget > _spinRate01 ? RollSpinUpTime : RollSpinDownTime;
		float spinStep = Time.Delta / MathF.Max( 0.01f, spinSecs );
		_spinRate01 += Math.Clamp( spinTarget - _spinRate01, -spinStep, spinStep );

		float amp = speed01 * RollVolume * Volume;
		if ( amp <= 0.0005f )
		{
			Array.Clear( _rollBuf, 0, want );           // keep the stream primed but silent
			_roll.WriteData( _rollBuf.AsSpan( 0, want ) );
			return;
		}

		float steer01 = _ball.SteerEffort01;            // non-zero only while still steering (pre-lock)
		// DEEP rumble: a ~40-90 Hz cutoff; a cutoff in the hundreds of Hz reads as wind.
		float cutoff = 0.006f + speed01 * 0.007f;
		float amInc = (24f + steer01 * 26f) / SR;       // wobble rate, faster while steering
		// "whump whump whump": one amplitude pulse per ball revolution. Spin rate tracks speed, so the
		// whumps speed up and slow down with the ball — based on velocity, not a real rotation read.
		float whumpInc = _spinRate01 * RollWhumpRate / SR;

		for ( int n = 0; n < want; n++ )
		{
			float white = NextFloat() * 2f - 1f;
			_lp1 += cutoff * (white - _lp1);
			_lp2 += cutoff * (_lp1 - _lp2);             // two-pole → deep brown rumble

			_amPhase += amInc; if ( _amPhase >= 1f ) _amPhase -= 1f;
			float wobble = 1f + steer01 * SteerRumble * MathF.Sin( _amPhase * MathF.PI * 2f );

			_whumpPhase += whumpInc; if ( _whumpPhase >= 1f ) _whumpPhase -= 1f;
			// raised-cosine swelled then sharpened (^3) into a thump that punches once per turn
			float pulse = 0.5f + 0.5f * MathF.Cos( _whumpPhase * MathF.PI * 2f );
			float whump = 1f + RollWhump * (pulse * pulse * pulse);

			float s = _lp2 * 9f * amp * wobble * whump;  // *9: makeup for the deep double-LP's level loss
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
