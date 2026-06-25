using Sandbox;
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
	[Property, Range( -89f, 89f, 1f )] public float MinPitch { get; set; } = -15f;
	/// <summary>Highest the camera can pitch (look up), in degrees. Slider so it's tunable in-scene.</summary>
	[Property, Range( -89f, 89f, 1f )] public float MaxPitch { get; set; } = 35f;

	private GameObject _target;
	private float _yaw;
	private float _pitch = 15f;

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

		var look = Input.AnalogLook; // sign + sensitivity already applied
		_yaw += look.yaw;
		_pitch = ( _pitch - look.pitch ).Clamp( MinPitch, MaxPitch );

		// Horizon-locked: roll stays 0.
		var rot = Rotation.From( _pitch, _yaw, 0f );
		var focus = _target.WorldPosition + Vector3.Up * Height;

		WorldPosition = focus - rot.Forward * Distance;
		WorldRotation = rot;
	}
}
