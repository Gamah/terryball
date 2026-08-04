using Sandbox;

namespace Terryball;

/// <summary>
/// A walk-up interaction spot at the MORE podium on the spawn concourse: stand within
/// <see cref="InteractRange"/> and press E to open the links screen — the Discord invite and our
/// other games, both click-to-copy (see <see cref="TerryAvatar"/> for the engage flow). The sixth
/// sibling of <see cref="LaneStation"/>, <see cref="BallDesignerStation"/>, <see cref="HelpStation"/>,
/// <see cref="TeleportStation"/> and <see cref="WorldSettingsStation"/> — TerryAvatar picks whichever
/// podium is nearest, so their ranges are kept tight to tell them apart.
/// </summary>
public sealed class MoreStation : Component
{
	/// <summary>Walk-up range (units) within which E opens the links screen.</summary>
	[Property] public float InteractRange { get; set; } = 130f;
}
