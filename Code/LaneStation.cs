using Sandbox;

namespace Terryball;

/// <summary>
/// A walk-up interaction spot at a lane's settee: stand within <see cref="InteractRange"/> and a
/// "Press E" prompt appears; pressing E opens that lane's controls panel and frees the mouse cursor
/// (see <see cref="TerryAvatar"/> for the engage flow). Screen-space buttons aren't clickable on their
/// own — the FPS look-lock keeps the cursor trapped — so this is how the player gets a cursor, the same
/// walk-up-and-press-E pattern as rotaliate's settings boards.
///
/// It lives under its <see cref="Lane"/> subtree (built by <see cref="LaneBuilder"/>), so for N lanes
/// each lane has its own station and the engaged station identifies which lane's controls to show.
/// </summary>
public sealed class LaneStation : Component
{
	/// <summary>Walk-up range (units) within which the "Press E" prompt shows and E engages.</summary>
	[Property] public float InteractRange { get; set; } = 160f;
}
