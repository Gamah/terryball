using Sandbox;

namespace Terryball;

/// <summary>
/// A walk-up podium (on the spawn concourse) that opens the lane-TELEPORT screen: stand within
/// <see cref="InteractRange"/> and press E to pop a panel of buttons that whisk you to any lane's settee.
/// The fourth walk-up station alongside <see cref="LaneStation"/>, <see cref="BallDesignerStation"/> and
/// <see cref="HelpStation"/> — TerryAvatar picks whichever podium is nearest, so ranges are kept tight.
/// </summary>
public sealed class TeleportStation : Component
{
	/// <summary>Walk-up range (units) within which E opens the teleport screen.</summary>
	[Property] public float InteractRange { get; set; } = 160f;
}
