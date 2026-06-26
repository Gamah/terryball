using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// Rides each player's networked walking-Terry object (a <see cref="PlayerController"/> Citizen).
/// Carries their display name + the two state bits the director (host) flips, AND is the networked
/// conduit for the active bowler's throw (it's owned by that client, so its RPCs originate there):
///   <see cref="DisplayName"/> — Steam name, host-set on spawn.
///   <see cref="Joined"/>      — opted into the next game's roster (owner-controlled).
///   <see cref="Bowling"/>     — currently up, riding the ball (host-set; exactly one true).
///
/// While a player is Bowling their walking body steps aside — hidden (their Terry appears riding
/// inside the glass ball instead) and their PlayerController suspended so the ball owns the throw.
///
/// Throw networking (approach B — the bowler is authoritative for their own turn):
///   <see cref="ReportBall"/>  — bowler → host: a settled ball's pinfall + whether the turn's done.
///   <see cref="StreamThrow"/> — bowler → everyone: ball + pin transforms, so spectators see it.
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

	private GameDirector Director => Scene?.GetAllComponents<GameDirector>().FirstOrDefault();

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

	/// <summary>Bowler → host: a settled ball knocked <paramref name="knocked"/> pins, and the bowler's
	/// own rules say their turn is <paramref name="turnDone"/>. The host books it onto the scorecard
	/// (and rotates to the next bowler when done). Runs on the host.</summary>
	[Rpc.Host]
	public void ReportBall( int knocked, int downMask, bool turnDone )
		=> Director?.HostBookBall( knocked, downMask, turnDone );

	/// <summary>Bowler → everyone: stream the live ball + pin transforms (and current down-mask) so
	/// spectators watch the throw on their frozen local copies. Unreliable + throttled; a dropped
	/// packet just means one skipped frame. Encoded as a flat comma-separated float list.</summary>
	[Rpc.Broadcast( NetFlags.Unreliable )]
	public void StreamThrow( string data, int downMask )
	{
		// Only apply on clients who AREN'T the bowler — the bowler has the real physics.
		if ( IsProxy )
			Director?.ApplyStreamedThrow( data, downMask );
	}
}
