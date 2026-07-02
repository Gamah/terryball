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
/// Spawned at the SCENE ROOT (not parented into the lane subtree): a networked object parented under a
/// non-networked scene object doesn't replicate cleanly to late-joiners, so the pin finds its lane via
/// <see cref="Lane.Local"/> instead and registers with that lane's builder.
///
/// The catch: the render/collision MESH is the lane's runtime-lathed PROCEDURAL model, and procedural
/// models do NOT replicate across the network (they aren't serialisable). So a freshly-replicated pin
/// arrives on a proxy with a null model and renders as the purple ERROR checker. The fix is here: every
/// client re-attaches its OWN local copy of that identical, deterministic model in <see cref="OnStart"/>
/// via <see cref="LaneBuilder.ConfigurePin"/>. The object is networked; the mesh is rebuilt locally.
/// </summary>
public sealed class Pin : Component
{
	/// <summary>This pin's number, 1 (head) to 10. Host-set at spawn and synced, so every client orders
	/// the rack identically for the director's scoring and respotting.</summary>
	[Sync] public int Number { get; set; }

	/// <summary>The world spot this pin racks on. Host-set at spawn; the director respots the pin here
	/// between balls. Synced so the spot is identical on every client.</summary>
	[Sync] public Vector3 Home { get; set; }

	/// <summary>The id of the <see cref="Lane"/> root this pin belongs to. Host-set at spawn and synced,
	/// so a root-spawned pin (which can't resolve its lane by parent) registers with the RIGHT lane's
	/// builder — essential once there's more than one lane, or lane 2's pins would attach to lane 1.</summary>
	[Sync] public System.Guid LaneId { get; set; }

	private LaneBuilder _builder;

	protected override void OnStart()
	{
		// Resolve our lane by the synced LaneId (we're root-spawned, so we can't go by parent) and register
		// so the director can find us even while we're swept off the deck (disabled) — a scene-wide
		// component scan skips disabled objects, which would lose swept pins and break the re-rack. Fall
		// back to the single local lane for the pre-multi-lane case / editor.
		_builder = (Lane.ById( Scene, LaneId ) ?? Lane.Of( this ) ?? Lane.Local( Scene ))?.Builder;
		_builder?.ConfigurePin( this ); // attach the local procedural mesh + physics (re-skins proxies)
		_builder?.RegisterPin( this );
	}

	protected override void OnDestroy() => _builder?.UnregisterPin( this );
}
