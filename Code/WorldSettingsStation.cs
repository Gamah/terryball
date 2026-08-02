using Sandbox;

namespace Terryball;

/// <summary>
/// A walk-up interaction spot at the SETTINGS podium on the spawn concourse: stand within
/// <see cref="InteractRange"/> and press E to open the world-settings screen (see <see cref="TerryAvatar"/>
/// for the engage flow). The fifth sibling of <see cref="LaneStation"/>, <see cref="BallDesignerStation"/>,
/// <see cref="HelpStation"/> and <see cref="TeleportStation"/> — TerryAvatar picks whichever podium is
/// nearest, so their ranges are kept tight to tell them apart.
/// </summary>
public sealed class WorldSettingsStation : Component
{
	/// <summary>Walk-up range (units) within which E opens the world-settings screen.</summary>
	[Property] public float InteractRange { get; set; } = 130f;
}
