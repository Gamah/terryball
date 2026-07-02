using Sandbox;

namespace Terryball;

/// <summary>
/// A walk-up interaction spot at the design table behind the settee: stand within
/// <see cref="InteractRange"/> and a "Press E — Ball Designer" prompt appears; pressing E opens the
/// ball-designer screen and frees the cursor (see <see cref="TerryAvatar"/> for the engage flow). It's
/// the sibling of <see cref="LaneStation"/> (the lane-controls podium) — TerryAvatar picks whichever
/// station is nearest, so the two are kept on their own tighter ranges to make them easy to tell apart.
/// </summary>
public sealed class BallDesignerStation : Component
{
	/// <summary>Walk-up range (units) within which the prompt shows and E engages the designer.</summary>
	[Property] public float InteractRange { get; set; } = 130f;
}
