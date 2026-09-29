using Sandbox;

namespace Terryball;

/// <summary>
/// A walk-up interaction spot at the MORE podium on the spawn concourse: stand within
/// <see cref="InteractRange"/> and press E to open the links screen — the Discord invite and our
/// other games and Skafinity, all click-to-copy (see <see cref="TerryAvatar"/> for the engage flow).
/// TerryAvatar picks the nearest podium of any kind, so ranges are kept tight to tell them apart.
/// </summary>
public sealed class MoreStation : Component
{
	/// <summary>Walk-up range (units) within which E opens the links screen.</summary>
	[Property] public float InteractRange { get; set; } = 130f;
}
