using Sandbox;

namespace Terryball;

/// <summary>
/// A walk-up interaction spot at the HELP podium on the settee: stand within <see cref="InteractRange"/>
/// and press E to open the how-to-play screen (see <see cref="TerryAvatar"/> for the engage flow). The
/// third sibling of <see cref="LaneStation"/> and <see cref="BallDesignerStation"/> — TerryAvatar picks
/// whichever of the three podiums is nearest, so their ranges are kept tight to tell them apart.
/// </summary>
public sealed class HelpStation : Component
{
	/// <summary>Walk-up range (units) within which E opens the help screen.</summary>
	[Property] public float InteractRange { get; set; } = 130f;
}
