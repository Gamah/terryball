using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// The per-lane hub. Each bowling lane is a self-contained subtree rooted on a GameObject that
/// carries this component: its geometry + pins (<see cref="LaneBuilder"/>), its ball
/// (<see cref="BallController"/>) and ball camera (<see cref="BallCamera"/>), its scoring authority
/// (<see cref="GameDirector"/>) and its settee feed camera (<see cref="LaneMonitorCamera"/>) all live
/// UNDER this object.
///
/// Every lane-scoped component resolves its siblings through its Lane (walk up to the Lane root, then
/// look within that subtree) instead of scanning the whole scene, so dropping a second Lane subtree
/// into the alley "just works" — nothing reaches across lanes by a scene-global FirstOrDefault, and
/// each director is authoritative only for ITS own ball/pins/active-bowler.
///
/// Only one lane is instantiated today; all the lookups are written for N.
/// </summary>
public sealed class Lane : Component
{
	/// <summary>The lane a component belongs to: the <see cref="Lane"/> on itself or any ancestor.</summary>
	public static Lane Of( Component c )
		=> c.IsValid() ? c.GetComponentInParent<Lane>( includeDisabled: true ) : null;

	/// <summary>The lane whose HUD / controls the LOCAL player should see — the lane the player has
	/// joined (or is currently walked up to). With N lanes a player moves between them, so this follows
	/// their own avatar rather than a fixed lane. Falls back to <see cref="Local"/> before an avatar
	/// exists / when nobody's joined, so the single-lane case "just works".</summary>
	public static Lane ForLocalPlayer( Scene scene )
		=> TerryAvatar.Local( scene )?.ActiveLane ?? Local( scene );

	/// <summary>The lane whose root GameObject carries <paramref name="id"/>, or null. How a per-player
	/// lane assignment (a synced lane id on the avatar) resolves back to the live lane.</summary>
	public static Lane ById( Scene scene, System.Guid id )
		=> id == System.Guid.Empty
			? null
			: scene?.GetAllComponents<Lane>().FirstOrDefault( l => l.IsValid() && l.GameObject.Id == id );

	/// <summary>The only lane in the scene (the single-lane fallback). Prefer <see cref="ForLocalPlayer"/>
	/// for anything player-facing so it follows the player across lanes.</summary>
	public static Lane Local( Scene scene )
		=> scene?.GetAllComponents<Lane>().FirstOrDefault();

	private LaneBuilder _builder;
	private BallController _ball;
	private BallCamera _camera;
	private GameDirector _director;
	private LaneMonitorCamera _monitor;
	private AnnouncerAudio _announcer;

	/// <summary>This lane's geometry/pin builder.</summary>
	public LaneBuilder Builder => _builder.IsValid() ? _builder : (_builder = GetComponentInChildren<LaneBuilder>( true ));
	/// <summary>This lane's ball.</summary>
	public BallController Ball => _ball.IsValid() ? _ball : (_ball = GetComponentInChildren<BallController>( true ));
	/// <summary>The single, shared ball camera (one main view for the whole alley — see BallCamera). It's not
	/// per-lane, so this resolves it scene-wide rather than within the lane subtree; every lane's director
	/// drives the same camera's Watching flag when its local bowler is up.</summary>
	public BallCamera Camera => _camera.IsValid() ? _camera : (_camera = Scene.GetAllComponents<BallCamera>().FirstOrDefault( c => c.IsValid() ));
	/// <summary>This lane's scoring/turn director (host-authoritative for this lane).</summary>
	public GameDirector Director => _director.IsValid() ? _director : (_director = GetComponentInChildren<GameDirector>( true ));
	/// <summary>This lane's settee feed camera (renders the active ball to the right-hand screen).</summary>
	public LaneMonitorCamera Monitor => _monitor.IsValid() ? _monitor : (_monitor = GetComponentInChildren<LaneMonitorCamera>( true ));
	/// <summary>This lane's announcer voice-over bank (the director routes settled-ball calls here).</summary>
	public AnnouncerAudio Announcer => _announcer.IsValid() ? _announcer : (_announcer = GetComponentInChildren<AnnouncerAudio>( true ));
}
