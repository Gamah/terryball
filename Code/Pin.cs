using Sandbox;

namespace Terryball;

/// <summary>
/// One of a lane's ten pins, as a single NETWORKED object. The pin's GameObject (transform +
/// Rigidbody) is spawned once by the host and replicated to every client; whoever is bowling owns all
/// ten for the duration of their turn (the director hands ownership over at <c>BeginTurn</c>), so the
/// active bowler simulates the whole crash locally and the engine streams the result — reliable,
/// interpolated, no hand-rolled transform broadcast. Spectators (the host included) see the one shared
/// rack, not a private local copy slammed to ~20 Hz samples.
///
/// The catch: the render/collision MESH is the lane's runtime-lathed PROCEDURAL model, and procedural
/// models do NOT replicate across the network (they aren't serialisable). So a freshly-replicated pin
/// arrives on a proxy with a null model and renders as the purple ERROR checker. The fix is here: every
/// client — owner and proxy alike — re-attaches its OWN local copy of that identical, deterministic
/// model in <see cref="OnStart"/> via <see cref="LaneBuilder.ConfigurePin"/>. The object is networked;
/// the mesh is rebuilt locally.
/// </summary>
public sealed class Pin : Component
{
	/// <summary>This pin's number, 1 (head) to 10. Host-set at spawn and synced, so every client orders
	/// the rack identically for the director's scoring and respotting.</summary>
	[Sync] public int Number { get; set; }

	/// <summary>The world spot this pin racks on. Host-set at spawn; the director respots the pin here
	/// between balls. Synced so the spot is identical on every client.</summary>
	[Sync] public Vector3 Home { get; set; }

	protected override void OnStart()
	{
		// Attach the lane's local procedural model + the pin's physics/audio config. Runs on every
		// client (owner and proxy), which is what re-skins the replicated proxy pins off the ERROR model.
		// Resolve via the parent subtree, but fall back to the single local lane in case a network proxy
		// didn't inherit the scene-parent reference (today there's only one lane, so this can't be wrong).
		var lane = Lane.Of( this ) ?? Lane.Local( Scene );
		lane?.Builder?.ConfigurePin( this );
	}
}
