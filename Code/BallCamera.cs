using Sandbox;
using System;
using System.Linq;

namespace Terryball;

/// <summary>
/// Fixed alley camera. It sits behind the run-up (just back of the throw position) up at eye
/// height and watches the pins down the lane — it does NOT chase the ball. The ball rolls away
/// from the camera toward the pins and the view stays put, the way you'd watch a real throw.
///
/// The mouse can still nudge the aim within a small cone (clamped yaw/pitch offsets around the
/// look-at-pins direction) so you can glance around, but it always recentres on the pins.
/// Horizon-locked: roll stays 0, so "forward" projected flat is reliably down-lane and the
/// camera-relative steering in <see cref="BallController"/> stays predictable.
/// </summary>
public sealed class BallCamera : Component
{
	/// <summary>How far BEHIND the run-up the camera sits (down-lane is +x, so behind is −x).</summary>
	[Property] public float BackDistance { get; set; } = 550f;
	/// <summary>Camera height above the platform surface.</summary>
	[Property] public float Height { get; set; } = 360f;
	/// <summary>How far (degrees) the mouse can swing the aim left/right off the pins.</summary>
	[Property, Range( 0f, 80f, 1f )] public float LookYawRange { get; set; } = 35f;
	/// <summary>How far (degrees) the mouse can tilt the aim up/down off the pins.</summary>
	[Property, Range( 0f, 60f, 1f )] public float LookPitchRange { get; set; } = 22f;

	// --- Watch mode -------------------------------------------------------------------------
	// While the director is watching the hit, the camera eases DOWN-LANE to a vantage just behind
	// and above the pin deck for a close overview of the strike — the lane is long, so the play
	// camera back at the run-up sees the pins only distantly. Look input is frozen so the shot stays
	// steady, and we ease between the play and watch positions instead of snapping.
	[Property, Group( "Watch" )] public float WatchBack { get; set; } = 1400f;  // how far behind the deck
	[Property, Group( "Watch" )] public float WatchHeight { get; set; } = 700f; // and up, for the overview
	[Property, Group( "Watch" )] public float WatchLerp { get; set; } = 4f;      // ease rate (per second)

	/// <summary>Driven by the round director: ease to the pin-deck overview and stop reading look.</summary>
	public bool Watching { get; set; }

	private LaneBuilder _lane;
	private float _yawOffset;
	private float _pitchOffset;
	private Vector3 _pos;     // eased camera position
	private bool _posInit;

	protected override void OnEnabled()
	{
		_lane = Scene.GetAllComponents<LaneBuilder>().FirstOrDefault();
	}

	protected override void OnUpdate()
	{
		if ( !_lane.IsValid() )
		{
			_lane = Scene.GetAllComponents<LaneBuilder>().FirstOrDefault();
			if ( !_lane.IsValid() ) return;
		}

		// Freeze look input while watching; otherwise the mouse nudges the aim within its cone.
		if ( !Watching )
		{
			var look = Input.AnalogLook; // sign + sensitivity already applied
			_yawOffset = (_yawOffset + look.yaw).Clamp( -LookYawRange, LookYawRange );
			_pitchOffset = (_pitchOffset - look.pitch).Clamp( -LookPitchRange, LookPitchRange );
		}

		// Play vantage: behind the run-up, raised. Watch vantage: behind + above the pin deck.
		var runUp = _lane.RunUpWorldPosition;
		var deck = _lane.PinDeckWorldCenter;
		var playPos = new Vector3( runUp.x - BackDistance, runUp.y, runUp.z + Height );
		var watchPos = new Vector3( deck.x - WatchBack, deck.y, deck.z + WatchHeight );
		var target = Watching ? watchPos : playPos;

		// Ease between the two vantages so entering/leaving watch glides.
		if ( !_posInit ) { _pos = target; _posInit = true; }
		_pos = Vector3.Lerp( _pos, target, MathF.Min( 1f, WatchLerp * Time.Delta ) );

		// Aim at the pin deck, then add the clamped mouse offsets. Horizon-locked (roll 0).
		var toPins = deck - _pos;
		var aim = Rotation.LookAt( toPins.Normal, Vector3.Up ).Angles();
		var rot = Rotation.From( (aim.pitch + _pitchOffset).Clamp( -89f, 89f ), aim.yaw + _yawOffset, 0f );

		WorldPosition = _pos;
		WorldRotation = rot;
	}
}
