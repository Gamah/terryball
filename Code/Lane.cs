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

	/// <summary>The lane whose HUD / controls the LOCAL player should see. Today that's simply the only
	/// lane; this is the single seam where N-lane player→lane assignment will pick among lanes.</summary>
	public static Lane Local( Scene scene )
		=> scene?.GetAllComponents<Lane>().FirstOrDefault();

	private LaneBuilder _builder;
	private BallController _ball;
	private BallCamera _camera;
	private GameDirector _director;
	private LaneMonitorCamera _monitor;

	/// <summary>This lane's geometry/pin builder.</summary>
	public LaneBuilder Builder => _builder.IsValid() ? _builder : (_builder = GetComponentInChildren<LaneBuilder>( true ));
	/// <summary>This lane's ball.</summary>
	public BallController Ball => _ball.IsValid() ? _ball : (_ball = GetComponentInChildren<BallController>( true ));
	/// <summary>This lane's ball camera.</summary>
	public BallCamera Camera => _camera.IsValid() ? _camera : (_camera = GetComponentInChildren<BallCamera>( true ));
	/// <summary>This lane's scoring/turn director (host-authoritative for this lane).</summary>
	public GameDirector Director => _director.IsValid() ? _director : (_director = GetComponentInChildren<GameDirector>( true ));
	/// <summary>This lane's settee feed camera (renders the active ball to the right-hand screen).</summary>
	public LaneMonitorCamera Monitor => _monitor.IsValid() ? _monitor : (_monitor = GetComponentInChildren<LaneMonitorCamera>( true ));
}
