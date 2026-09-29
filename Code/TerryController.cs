using Sandbox;

namespace Terryball;

/// <summary>
/// Holds Terry on the floor: gravity plus a clamp at z = 0. Takes no player input.
/// </summary>
public sealed class TerryController : Component
{
	[Property] public float Gravity { get; set; } = 800f;

	private Vector3 _velocity;

	protected override void OnUpdate()
	{
		_velocity.z -= Gravity * Time.Delta;
		WorldPosition += _velocity * Time.Delta;

		if ( WorldPosition.z < 0f )
		{
			WorldPosition = WorldPosition.WithZ( 0f );
			_velocity.z = 0f;
		}
	}
}
