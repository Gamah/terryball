using Sandbox;

namespace Terryball;

/// <summary>
/// Rides each player's networked walking-Terry object (a <see cref="PlayerController"/> Citizen).
/// Carries their display name and the two state bits the director (host) flips:
///   <see cref="Joined"/>  — opted into the next game's roster via the lane console.
///   <see cref="Bowling"/> — currently the active bowler, up and riding the shared ball.
///
/// Both are host-authoritative ([Sync] FromHost): the director decides who's joined and who's up,
/// and every client reads it. While a player is Bowling their walking body steps aside — it's
/// hidden (their Terry appears riding inside the glass ball instead) and their PlayerController is
/// suspended so the <see cref="BallController"/>/<see cref="BallCamera"/> own the throw.
/// </summary>
public sealed class TerryAvatar : Component
{
	/// <summary>Steam display name, set by the host on spawn so the scoreboard/banner can use it.</summary>
	[Sync( SyncFlags.FromHost )] public string DisplayName { get; set; } = "Bowler";

	/// <summary>Opted into the next game's roster (toggled from the lane controls before START). Owner-
	/// controlled — each client flips its OWN avatar's flag directly; the host reads it on START.</summary>
	[Sync] public bool Joined { get; set; }

	/// <summary>Currently up — riding the ball, not walking. The director sets exactly one true.</summary>
	[Sync( SyncFlags.FromHost )] public bool Bowling { get; set; }

	private PlayerController _player;
	private GameObject _body;

	protected override void OnStart()
	{
		_player = Components.Get<PlayerController>();
		_body = _player.IsValid() && _player.Renderer.IsValid() ? _player.Renderer.GameObject : null;
	}

	protected override void OnUpdate()
	{
		// Hand the screen + control to the ball while this player is up: suspend their walk/camera
		// controls and hide the walking body. Applied on every client — the hide must be visible to
		// all; the control-suspend only actually matters on the owner (proxies don't drive anyway).
		if ( _player.IsValid() )
			_player.Enabled = !Bowling;
		if ( _body.IsValid() )
			_body.Enabled = !Bowling;
	}
}
