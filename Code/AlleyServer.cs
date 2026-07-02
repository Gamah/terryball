using Sandbox;
using System;
using System.Linq;

namespace Terryball;

/// <summary>
/// The one server-level authority for the whole alley. Exactly ONE of these lives on the scene root, and
/// it owns the responsibilities that are per-SESSION rather than per-lane:
///   • spawning one walking Terry per connected player (and despawning it on leave).
///
/// This used to live on the <see cref="GameDirector"/> (via INetworkListener), but that fires on EVERY
/// component that implements it — so with N lanes each joiner got N avatars. Centralising it here means a
/// player joins the server once, gets one Terry in the spawn area, and walks up the stairs to whichever
/// lane they like. Whether they BOWL on a lane is a separate opt-in (JOIN at that lane's station, then a
/// player claims the lane's director and presses START).
/// </summary>
public sealed class AlleyServer : Component, Component.INetworkListener
{
	/// <summary>Where joiners spawn — the back-of-house spawn area. Players fan out around this point and
	/// walk up the stairs to a lane. If unset, falls back to the first lane's settee.</summary>
	[Property] public GameObject SpawnArea { get; set; }

	/// <summary>Half-width (units) of the spread joiners are fanned across at the spawn point.</summary>
	[Property] public float SpawnSpread { get; set; } = 120f;

	private int _spawnCount; // players dropped so far, to fan them out

	protected override void OnStart()
	{
		// Auto-host so the scene is playable solo (you become the host + first bowler) and ready to accept
		// joiners. If the scene was loaded BY joining someone else's lobby, a session is already active and
		// we leave it alone. Done ONCE here rather than per-lane-director (N directors would race it).
		if ( !Networking.IsActive )
			Networking.CreateLobby( new Sandbox.Network.LobbyConfig() );
	}

	/// <summary>Host-side: a player finished joining — drop them a walking Terry in the spawn area.</summary>
	public void OnActive( Connection channel ) => SpawnAvatar( channel );

	/// <summary>Host-side: a player left — remove their Terry. Any lane they were the director/active bowler
	/// on recovers on its own: director ownership reverts to the host, and each director polls for a
	/// departed active bowler (see GameDirector.OnUpdate).</summary>
	public void OnDisconnected( Connection channel )
	{
		foreach ( var av in Scene.GetAllComponents<TerryAvatar>().ToList() )
			if ( av.IsValid() && av.GameObject.Network.Owner?.Id == channel.Id )
				av.GameObject.Destroy();
	}

	/// <summary>Build one player's walking-Terry avatar (a PlayerController Citizen) at a spawn spot and
	/// hand network ownership to them. Host only — the spawn replicates to every client.</summary>
	private void SpawnAvatar( Connection channel )
	{
		if ( !Networking.IsHost ) return;

		var go = new GameObject( true, $"Terry_{channel.DisplayName}" )
		{
			WorldPosition = NextSpawnPosition( out var facing ),
			WorldRotation = facing,
		};

		var player = go.Components.Create<PlayerController>();

		// Citizen body the PlayerController animates (mirrors its own CreateBodyRenderer()).
		var body = new GameObject( true, "Body" ) { Parent = go };
		var renderer = body.Components.Create<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );
		player.Renderer = renderer;

		var avatar = go.Components.Create<TerryAvatar>();
		avatar.DisplayName = channel.DisplayName;

		go.NetworkSpawn( channel );
	}

	// Fan spawned players across the spawn area so they don't pile on one spot.
	private Vector3 NextSpawnPosition( out Rotation facing )
	{
		Vector3 center;
		if ( SpawnArea.IsValid() )
		{
			center = SpawnArea.WorldPosition;
			facing = SpawnArea.WorldRotation;
		}
		else
		{
			// Fallback (no spawn area placed): the first lane's settee, facing down-lane.
			var builder = Lane.Local( Scene )?.Builder;
			center = builder.IsValid() ? builder.SetteeFloorCenter : Vector3.Zero;
			facing = builder.IsValid() ? builder.SetteeFaceRotation : Rotation.Identity;
		}

		var slot = _spawnCount++;
		// Zig-zag out from the centre: 0, +1, -1, +2, -2, … so early joiners cluster near the middle.
		var step = (slot + 1) / 2 * ((slot % 2 == 0) ? 1 : -1);
		var t = (SpawnSpread > 1f) ? Math.Clamp( step * 90f, -SpawnSpread, SpawnSpread ) : 0f;
		return center + facing.Left * t;
	}
}
