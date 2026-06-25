using Sandbox;
using System;
using System.Linq;

namespace Terryball;

/// <summary>
/// Third-person follow camera for the ball. Horizon-locked (never rolls): the mouse yaws
/// around the target and pitches to look up/down. Movement input in <see cref="BallController"/>
/// is taken relative to this camera's facing, so "forward" is always where you're looking.
/// </summary>
public sealed class BallCamera : Component
{
	[Property] public float Distance { get; set; } = 700f;
	[Property] public float Height { get; set; } = 60f;
	/// <summary>Lowest the camera can pitch (look down), in degrees. Slider so it's tunable in-scene.</summary>
	[Property, Range( -89f, 89f, 1f )] public float MinPitch { get; set; } = -5f;
	/// <summary>Highest the camera can pitch (look up), in degrees. Slider so it's tunable in-scene.</summary>
	[Property, Range( -89f, 89f, 1f )] public float MaxPitch { get; set; } = 15f;

	// --- Watch mode -------------------------------------------------------------------------
	// While the director is watching the hit, the camera pulls back and up and tilts down for an
	// overview of the pin deck. Look input is frozen so the shot stays steady, and we ease between
	// the play framing and the watch framing instead of snapping.
	[Property, Group( "Watch" )] public float WatchDistance { get; set; } = 1700f;
	[Property, Group( "Watch" )] public float WatchHeight { get; set; } = 650f;
	[Property, Group( "Watch" )] public float WatchPitch { get; set; } = 38f; // tilt down onto the pins
	[Property, Group( "Watch" )] public float WatchLerp { get; set; } = 4f;    // ease rate (per second)

	/// <summary>Driven by the round director: ease to the pulled-back overview and stop reading look.</summary>
	public bool Watching { get; set; }

	private GameObject _target;
	private float _yaw;
	private float _pitch = 15f;
	private float _dist;
	private float _height;

	protected override void OnAwake()
	{
		_dist = Distance;
		_height = Height;
	}

	protected override void OnEnabled()
	{
		// Follow the ball without needing a wired reference.
		var ball = Scene.GetAllComponents<BallController>().FirstOrDefault();
		_target = ball?.GameObject;
	}

	protected override void OnUpdate()
	{
		if ( !_target.IsValid() )
		{
			var ball = Scene.GetAllComponents<BallController>().FirstOrDefault();
			_target = ball?.GameObject;
			if ( !_target.IsValid() ) return;
		}

		// Freeze look input while watching; otherwise yaw/pitch follow the mouse as normal.
		if ( !Watching )
		{
			var look = Input.AnalogLook; // sign + sensitivity already applied
			_yaw += look.yaw;
			_pitch = ( _pitch - look.pitch ).Clamp( MinPitch, MaxPitch );
		}

		// Ease the framing toward play or watch values so entering/leaving watch glides.
		var t = MathF.Min( 1f, WatchLerp * Time.Delta );
		var targetDist = Watching ? WatchDistance : Distance;
		var targetHeight = Watching ? WatchHeight : Height;
		_dist = MathX.Lerp( _dist, targetDist, t );
		_height = MathX.Lerp( _height, targetHeight, t );
		var pitch = Watching ? MathX.Lerp( _pitch, WatchPitch, t ) : _pitch;
		if ( Watching ) _pitch = pitch; // carry the eased pitch so it resumes smoothly afterwards

		// Horizon-locked: roll stays 0.
		var rot = Rotation.From( pitch, _yaw, 0f );
		var focus = _target.WorldPosition + Vector3.Up * _height;

		WorldPosition = focus - rot.Forward * _dist;
		WorldRotation = rot;
	}
}
