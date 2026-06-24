using Sandbox;

namespace Terryball;

/// <summary>
/// Terry, the player avatar. STUB ONLY.
///
/// The movement/input mechanic is deliberately left unimplemented — see PLAN.md
/// ("Input mechanic"). Do NOT fill this in by copying a Super Monkey Ball control
/// scheme; the intended feel diverges fundamentally (PLAN.md spells out how).
///
/// For now this just keeps Terry upright and on the floor so the scene runs.
/// </summary>
public sealed class TerryController : Component
{
	[Property] public float Gravity { get; set; } = 800f;

	private Vector3 _velocity;

	protected override void OnUpdate()
	{
		// Placeholder: gravity + ground clamp only. No player input yet.
		_velocity.z -= Gravity * Time.Delta;
		WorldPosition += _velocity * Time.Delta;

		if ( WorldPosition.z < 0f )
		{
			WorldPosition = WorldPosition.WithZ( 0f );
			_velocity.z = 0f;
		}

		// TODO(input mechanic): drive Terry here. See PLAN.md before writing this.
	}
}
