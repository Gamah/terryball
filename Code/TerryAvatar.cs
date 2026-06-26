using Sandbox;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
	private bool _wasBowling;   // edge-detect the end of our turn, to walk us back to the settee

	// --- Walk-up lane controls (LOCAL player only) -------------------------------------------
	// Screen-space buttons can't be clicked while the FPS look-lock traps the cursor, so the local
	// bowler walks up to a LaneStation at the settee and presses E: that frees the cursor and opens the
	// controls. Pure local UI state — never networked.
	private bool _controlsOpen;
	private bool _nearStation;

	/// <summary>True while this (local) player has the lane-controls panel open and the cursor freed.</summary>
	public bool ControlsOpen => _controlsOpen;
	/// <summary>True while this (local) player is standing in a LaneStation's range (show the prompt).</summary>
	public bool NearStation => _nearStation;

	/// <summary>The local player's own (non-proxy) avatar, or null before it spawns.</summary>
	public static TerryAvatar Local( Scene scene )
		=> scene?.GetAllComponents<TerryAvatar>().FirstOrDefault( a => a.IsValid() && !a.IsProxy );

	// The director of the lane this avatar is playing on. With one lane that's the only lane; this is
	// the seam where N-lane assignment will pick the avatar's chosen lane (avatars aren't inside a Lane
	// subtree, so this can't be Lane.Of).
	private GameDirector Director => Lane.Local( Scene )?.Director;

	private SkinnedModelRenderer _renderer;
	private CancellationTokenSource _dressCts;

	protected override void OnStart()
	{
		_player = Components.Get<PlayerController>();
		_renderer = _player.IsValid() ? _player.Renderer : null;
		_body = _renderer.IsValid() ? _renderer.GameObject : null;

		_ = DressAvatar();
	}

	/// <summary>
	/// Dress this walking Terry in its owner's full Steam avatar — including workshop / Steam-inventory
	/// (community-market) cosmetics. Clothing objects aren't networked, so EVERY client dresses EVERY
	/// avatar locally: our own from <see cref="ClothingContainer.CreateFromLocalUser"/> (the complete
	/// local avatar), everyone else's from <see cref="ClothingContainer.CreateFromConnection"/> (their
	/// avatar user-data, filtered to items they actually own). <c>ApplyAsync</c> — not the synchronous
	/// <c>Apply</c> — first downloads any missing workshop/inventory items, matching the engine's own
	/// Dresser; sync Apply silently drops anything not already on disk.
	/// </summary>
	private async Task DressAvatar()
	{
		if ( !_renderer.IsValid() ) return;

		var clothing = !IsProxy
			? ClothingContainer.CreateFromLocalUser()
			: (Network.Owner is not null ? ClothingContainer.CreateFromConnection( Network.Owner ) : null);
		if ( clothing is null ) return;

		_dressCts?.Cancel();
		_dressCts = new CancellationTokenSource();
		try
		{
			await clothing.ApplyAsync( _renderer, _dressCts.Token );
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception e )
		{
			Log.Warning( $"[terryball] avatar dressing failed: {e.Message}" );
		}
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

		// Only the local player drives the walk-up controls (proxies are other people's avatars).
		if ( !IsProxy )
		{
			// Our turn just ended (handed off to the next bowler, or the game finished): put us back in
			// the settee, not stranded up at the lane station where we pressed START. The transform is
			// owner-authoritative, so we move ourselves rather than the host yanking us.
			if ( _wasBowling && !Bowling )
				ReturnToSettee();

			UpdateControlsStation();
		}
		_wasBowling = Bowling;
	}

	// Drop us back onto the settee carpet, fanned across its width by a stable per-player offset so
	// returning bowlers don't stack on one spot. The lane builder hands us the seating zone's geometry.
	private void ReturnToSettee()
	{
		var builder = Lane.Local( Scene )?.Builder;
		if ( !builder.IsValid() ) return;

		var half = builder.SetteeHalfWidthUnits;
		// SetteeHalfWidthUnits is 0 until the lane has actually built its geometry; teleporting then would
		// drop us at the world origin (under the floor → endless fall/respawn, which reads as a hang).
		if ( half <= 0f ) return;
		var spread = (Math.Abs( (Network.Owner?.Id.GetHashCode() ?? 0) ) % 1000) / 1000f; // 0..1, stable
		var y = MathX.Lerp( -half * 0.7f, half * 0.7f, spread );
		GameObject.WorldPosition = builder.SetteeFloorCenter + new Vector3( 0f, y, 0f );
		GameObject.WorldRotation = Rotation.FromYaw( 0f ); // face the lane (+x)
	}

	// Find the nearest in-range LaneStation and engage/disengage the lane-controls cursor on E.
	private void UpdateControlsStation()
	{
		// No fiddling with the panel while you're riding the ball.
		if ( Bowling )
		{
			if ( _controlsOpen ) CloseControls();
			_nearStation = false;
			return;
		}

		var station = NearestStation();
		_nearStation = station is not null;

		if ( _controlsOpen )
		{
			// E again, Escape, or walking out of range closes it and restores the look/cursor.
			if ( Input.Pressed( "use" ) || Input.EscapePressed || station is null )
			{
				Input.EscapePressed = false;
				CloseControls();
			}
			else
			{
				// Keep the cursor freed while the panel is open (re-assert in case anything resets it).
				Mouse.Visibility = MouseVisibility.Visible;
			}
		}
		else if ( _nearStation && Input.Pressed( "use" ) )
		{
			OpenControls();
		}
	}

	/// <summary>Close the controls panel from the panel's own CLOSE button (restores look + cursor).</summary>
	public void RequestCloseControls()
	{
		if ( _controlsOpen ) CloseControls();
	}

	private LaneStation NearestStation()
	{
		LaneStation best = null;
		var bestDist = float.MaxValue;
		var me = GameObject.WorldPosition;
		foreach ( var s in Scene.GetAllComponents<LaneStation>() )
		{
			if ( !s.IsValid() ) continue;
			var dist = s.WorldPosition.Distance( me );
			if ( dist < s.InteractRange && dist < bestDist )
			{
				best = s;
				bestDist = dist;
			}
		}
		return best;
	}

	// Free the cursor for the screen-space controls. Mouse.Visibility=Visible is what flips the engine's
	// input context to UI (so the buttons take clicks); UseLookControls=false stops the camera spinning
	// while the mouse drives the cursor instead of the view.
	private void OpenControls()
	{
		_controlsOpen = true;
		if ( _player.IsValid() ) _player.UseLookControls = false;
		Mouse.Visibility = MouseVisibility.Visible;
	}

	private void CloseControls()
	{
		_controlsOpen = false;
		if ( _player.IsValid() ) _player.UseLookControls = true;
		Mouse.Visibility = MouseVisibility.Auto;
	}

	// Never leave the cursor freed / look disabled if our avatar goes away while engaged.
	protected override void OnDisabled()
	{
		if ( _controlsOpen ) CloseControls();
	}

	protected override void OnDestroy()
	{
		if ( _controlsOpen ) CloseControls();
		_dressCts?.Cancel();
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
		if ( !IsProxy ) return;

		// And ONLY if this stream is from the player who's actually up. The outgoing bowler's last
		// (unreliable) packets can land just after the next bowler has racked a fresh deck — without this
		// they'd stomp those reset pins back to the previous leave, so the new turn starts mid-rack.
		var dir = Director;
		if ( !dir.IsValid() || GameObject.Network.Owner?.Id != dir.ActiveBowlerId ) return;

		dir.ApplyStreamedThrow( data, downMask );
	}
}
