using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// Bootstraps the one client-local HUD (see <see cref="LocalHud"/>) for each running game scene — purely
/// local, with NO dependency on anything networked.
///
/// Why a system and not a scene object or a director: a joining client doesn't load the scene from disk,
/// it rebuilds it from the host's network snapshot, which EXCLUDES NetworkMode.Never objects — so a
/// hand-placed Never HUD never reaches clients. And the HUD shouldn't have to hang off a networked
/// component's lifecycle (the director) just to exist either. A <see cref="GameObjectSystem"/> is
/// instantiated locally for every scene on every machine, independent of networking / ownership / how
/// many lanes there are, so it's the right place to spawn a strictly-local object exactly once per client.
/// </summary>
public sealed class LocalHudSystem : GameObjectSystem
{
	private bool _built;

	public LocalHudSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, EnsureHud, nameof( EnsureHud ) );
	}

	private void EnsureHud()
	{
		if ( _built ) return;
		if ( Scene is null || Scene.IsEditor ) return; // play scenes only, not the editor's edit scene

		// Idempotent against a hot-reload / re-entry that already made one.
		if ( !Scene.GetAllComponents<LocalHud>().Any() )
		{
			var go = new GameObject( true, "LocalHUD" ) { Flags = GameObjectFlags.NotSaved };
			go.NetworkMode = NetworkMode.Never; // strictly client-local — never replicated
			go.Components.Create<LocalHud>();
		}
		_built = true;
	}
}
