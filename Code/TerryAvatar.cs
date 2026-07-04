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
///   (the throw itself isn't streamed here — the bowler owns the lane's networked ball + rack and the
///   engine replicates them, so spectators just watch the shared objects.)
/// </summary>
public sealed class TerryAvatar : Component
{
	/// <summary>Steam display name, set by the host on spawn so the scoreboard/banner can use it.</summary>
	[Sync( SyncFlags.FromHost )] public string DisplayName { get; set; } = "Bowler";

	/// <summary>Opted into the next game's roster (toggled from the lane controls before START). Owner-
	/// controlled — each client flips its OWN avatar's flag directly; the host reads it on START.</summary>
	[Sync] public bool Joined { get; set; }

	/// <summary>The id of the <see cref="Lane"/> this player has joined (its root GameObject id), or
	/// <see cref="Guid.Empty"/> if not joined. Owner-set from the lane station they pressed JOIN at, and
	/// host-read so each lane's director only rosters ITS own joiners. This is the per-player lane
	/// assignment — with N lanes a player picks which lane they're playing on.</summary>
	[Sync] public Guid JoinedLaneId { get; set; }

	/// <summary>Currently up — riding the ball, not walking. DERIVED (not a stored/synced field) from
	/// this avatar's lane director's <see cref="GameDirector.ActiveBowlerId"/>: this avatar is bowling iff
	/// its owner is the one the director has spotlighted. It's derived rather than a <c>FromHost</c> flag
	/// because the director is no longer the host — it's a claimable, player-owned authority, and a
	/// non-host director can't write another player's avatar field. The single synced source (the
	/// director's ActiveBowlerId) drives it on every client instead.</summary>
	public bool Bowling
	{
		get
		{
			var id = ActiveLane?.Director?.ActiveBowlerId ?? Guid.Empty;
			return id != Guid.Empty && Network.Owner?.Id == id;
		}
	}

	/// <summary>The crane has scooped this bowler INTO the ball: hide their walking body and show the
	/// in-ball Terry instead. Owner-driven (the bowler runs its own pickup/dropoff crane) and synced so
	/// every client swaps body↔ball-rider at the same instant. Distinct from <see cref="Bowling"/>, which
	/// spans the whole turn — between Bowling going true and the claw actually arriving, the walking body
	/// is still sitting on the settee waiting to be picked up.</summary>
	[Sync] public bool Carried { get; set; }

	/// <summary>While this avatar is the active bowler, its OWNER publishes its live camera transform
	/// here so every settee monitor mirrors the bowler's actual eye view (see LaneMonitorCamera) rather
	/// than a static chase. Only meaningful while <see cref="Bowling"/>.</summary>
	[Sync] public Vector3 BowlCamPos { get; set; }
	[Sync] public Rotation BowlCamRot { get; set; } = Rotation.Identity;

	// --- Guided-throw ("Aim" mode) step meter, mirrored for the settee feed board -----------------------
	// While this bowler lines up a guided throw, their client-local ThrowAimer publishes the live meter state
	// here (see ThrowAimer.PublishBoard). The per-lane MonitorFeed board reads it off the active bowler and
	// draws a duplicate of the LINE/AIM/POWER/SPIN meter for the seated spectators, up for the same span as
	// the bowler's own HUD. Only meaningful while AimHudActive.
	[Sync] public bool AimHudActive { get; set; }
	[Sync] public int AimStepNet { get; set; }            // 1..4 (LINE/AIM/POWER/SPIN)
	[Sync] public float AimCaretNet { get; set; }         // live caret 0..1 (0 = screen-left)
	[Sync] public float AimSweetNet { get; set; } = -1f;  // sweet-spot fraction, or <0 for none
	[Sync] public bool AimTwoSidedNet { get; set; }       // SPIN: centred, hooks either way
	[Sync] public bool AimCommittedNet { get; set; }      // throw's away — frozen summary
	[Sync] public float AimLineCaretNet { get; set; }     // locked LINE caret (ghost track)
	[Sync] public float AimAimCaretNet { get; set; }      // locked AIM caret (ghost track)
	[Sync] public float AimPowerCaretNet { get; set; }    // locked POWER caret (ghost track)

	// --- Ball design (owner-chosen, networked so everyone's ball skins the same when they're up) -----
	// Each client picks its OWN look in the designer; it's saved client-local (BallDesign.Save) and
	// mirrored here so the shared ball (BallController) can skin itself to whoever is currently bowling.
	// Stored as three primitives (not the struct) to keep the sync trivial. Defaults match BallDesign.Default.
	[Sync] public int BallPatternId { get; set; } = (int)BallDesign.DefaultPattern;
	[Sync] public float BallPrimaryHue { get; set; } = BallDesign.DefaultPrimaryHue;
	[Sync] public float BallAccentHue { get; set; } = BallDesign.DefaultAccentHue;

	/// <summary>This avatar's ball design, assembled from the synced fields — read by BallController to
	/// skin the ball, and by the designer preview.</summary>
	public BallDesign Design => new()
	{
		Pattern = (BallPattern)BallPatternId,
		PrimaryHue = BallPrimaryHue,
		AccentHue = BallAccentHue,
	};

	/// <summary>Owner-side: push a new design onto the synced fields AND persist it to the local json.
	/// The designer panel calls this as sliders/patterns change (live) and on close.</summary>
	public void ApplyDesign( BallDesign d, bool save = true )
	{
		BallPatternId = (int)d.Pattern;
		BallPrimaryHue = d.PrimaryHue;
		BallAccentHue = d.AccentHue;
		if ( save ) d.Save();

		// Push straight to our lane's director so a live designer edit re-skins the ball immediately.
		SubmitDesignToDirector();
	}

	/// <summary>Register our current ball design with the lane we're on, via the director's owner RPC. The
	/// director (not this avatar) is the ball's skin source — its [Sync] fields reliably reach late joiners
	/// where the avatar's did not. No-op until we've joined a lane / networking is live; re-asserted
	/// periodically from OnUpdate so it also survives a director ownership handover.</summary>
	private void SubmitDesignToDirector()
	{
		if ( IsProxy || !Network.Active ) return;
		Director?.SetBallDesignRpc( BallPatternId, BallPrimaryHue, BallAccentHue );
	}

	private float _designPushAt;      // next time (Time.Now) to re-assert our design to the director

	private PlayerController _player;
	private GameObject _body;
	private bool _wasBowling;   // edge-detect the end of our turn, to walk us back to the settee
	private bool _wasCarried;   // edge-detect leaving the ball, to carry the view aim into the walk camera
	private bool _designPublished; // have we (re-)pushed our ball design AFTER networking went live? (see OnUpdate)
	private Rotation _lastInBallCamRot = Rotation.Identity; // ball-cam aim, cached while carried, for the hand-off

	// Where the crane grabbed us (or the settee middle, if we were grabbed off it) — the turn-end drop
	// AND the R/teleport fallback put us back here. Owner-local; set by the director at pickup.
	private Vector3 _returnSpot;
	private bool _hasReturnSpot;

	/// <summary>Director (owner-side) records the spot the claw should set us back down on.</summary>
	public void SetReturnSpot( Vector3 worldSpot ) { _returnSpot = worldSpot; _hasReturnSpot = true; }

	/// <summary>Director (owner-side): the claw reached us — scoop us into the ball.</summary>
	public void ScoopIntoBall() => Carried = true;

	/// <summary>Height (world units) of this walking avatar's pelvis bone above its foot origin. The crane
	/// uses it to drop the ball low enough to centre the pelvis (matching the in-ball framing), instead of
	/// resting the ball's bottom at the feet. Falls back to ~half a Citizen if the bone isn't resolvable.</summary>
	public float PelvisHeight =>
		_renderer.IsValid() && _renderer.TryGetBoneTransformLocal( "pelvis", out var p )
			? p.Position.z * _renderer.WorldScale.z
			: 36f;

	/// <summary>Director (owner-side): the claw reached our seat — set us back down on the recorded spot.</summary>
	public void DropAtReturnSpot() { Carried = false; ReturnToSettee(); }

	// --- Walk-up lane controls (LOCAL player only) -------------------------------------------
	// Screen-space buttons can't be clicked while the FPS look-lock traps the cursor, so the local
	// bowler walks up to a LaneStation at the settee and presses E: that frees the cursor and opens the
	// controls. Pure local UI state — never networked.
	private bool _controlsOpen;
	private bool _nearStation;
	private Lane _engagedLane;   // (local) the lane whose station we're currently walked up to
	private bool _designerOpen;  // (local) the ball-designer screen is up (cursor freed)
	private bool _nearDesigner;  // (local) standing in the design table's range (show the prompt)
	private bool _helpOpen;      // (local) the how-to-play screen is up (cursor freed)
	private bool _nearHelp;      // (local) standing in the help podium's range (show the prompt)
	private bool _teleportOpen;  // (local) the lane-teleport screen is up (cursor freed)
	private bool _nearTeleport;  // (local) standing in the teleport podium's range (show the prompt)

	/// <summary>True while this (local) player has the lane-controls panel open and the cursor freed.</summary>
	public bool ControlsOpen => _controlsOpen;
	/// <summary>True while this (local) player is standing in a LaneStation's range (show the prompt).</summary>
	public bool NearStation => _nearStation;
	/// <summary>True while this (local) player has the ball-designer screen open and the cursor freed.</summary>
	public bool DesignerOpen => _designerOpen;
	/// <summary>True while this (local) player is standing in a BallDesignerStation's range.</summary>
	public bool NearDesigner => _nearDesigner;
	/// <summary>True while this (local) player has the how-to-play screen open and the cursor freed.</summary>
	public bool HelpOpen => _helpOpen;
	/// <summary>True while this (local) player is standing in a HelpStation's range.</summary>
	public bool NearHelp => _nearHelp;
	/// <summary>True while this (local) player has the lane-teleport screen open and the cursor freed.</summary>
	public bool TeleportOpen => _teleportOpen;
	/// <summary>True while this (local) player is standing in a TeleportStation's range.</summary>
	public bool NearTeleport => _nearTeleport;

	/// <summary>The lane whose HUD / controls THIS (local) player should see: the lane they've joined if
	/// any, else the lane of the station they're currently walked up to, else null. The HUD reads this
	/// (via <see cref="Lane.ForLocalPlayer"/>) so the score/turn board follows the player across lanes.</summary>
	public Lane ActiveLane => Lane.ById( Scene, JoinedLaneId ) ?? _engagedLane;

	/// <summary>The local player's own (non-proxy) avatar, or null before it spawns.</summary>
	public static TerryAvatar Local( Scene scene )
		=> scene?.GetAllComponents<TerryAvatar>().FirstOrDefault( a => a.IsValid() && !a.IsProxy );

	// The director of the lane this avatar is playing on — its ActiveLane (avatars live on their own
	// networked object, not inside a Lane subtree, so this goes by the assigned lane id, not Lane.Of).
	private GameDirector Director => ActiveLane?.Director;

	private SkinnedModelRenderer _renderer;
	private CancellationTokenSource _dressCts;

	protected override void OnStart()
	{
		_player = Components.Get<PlayerController>();
		_renderer = _player.IsValid() ? _player.Renderer : null;
		_body = _renderer.IsValid() ? _renderer.GameObject : null;

		// NB: we deliberately DON'T publish our saved ball design here. This avatar is host-spawned with the
		// DEFAULT design, and a [Sync] write in OnStart lands too early to be sent as an owner change — it just
		// sets our local field to the real value, leaving the sync baseline equal to it, so no delta ever goes
		// out and every other client (incl. late joiners, off the host's snapshot) keeps the default skin
		// forever. Instead we leave the field at its synced default and make the FIRST write happen once
		// networking is live (OnUpdate), so it's a genuine default→real change that replicates.

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
		// Both the walk/camera controller AND the visible body hand off to the ball only once the crane has
		// actually scooped this player IN (Carried). Until then — including the whole pickup flight — they
		// stay walking on the settee, so their camera stays on their own Terry (not the empty claw) and
		// they watch the ball come to fetch them. Applied on every client so the swap is seen by all.
		if ( _player.IsValid() )
			_player.Enabled = !Carried;
		if ( _body.IsValid() )
			_body.Enabled = !Carried;

		// Only the local player drives the walk-up controls (proxies are other people's avatars).
		if ( !IsProxy )
		{
			// Publish our saved ball design as the FIRST write to these [Sync] fields, now that networking is
			// live — see the note in OnStart for why this can't be done there. The field was left at its synced
			// default, so this is a real default→real change: it replicates to the host and every client (and
			// so into the snapshot late joiners receive), and the shared ball skins to us on all of them.
			if ( !_designPublished && Network.Active )
			{
				ApplyDesign( BallDesign.Load(), save: false );
				_designPublished = true;
			}

			// Re-assert our design to the lane's director every so often. Cheap, and it self-heals every
			// gap the one-shot push can't cover: joining a lane after we spawned, and a director ownership
			// handover (the new owner's per-connection cache starts empty). No-op until we're on a lane.
			if ( Network.Active && Time.Now >= _designPushAt )
			{
				_designPushAt = Time.Now + 1.5f;
				SubmitDesignToDirector();
			}

			// Safety: if we're not up, we're not in the ball — never leave a non-bowling avatar stuck
			// invisible (e.g. STOP/disconnect mid-crane).
			if ( !Bowling && Carried )
				Carried = false;

			// Carry the view aim across the ball→walk hand-off so the camera doesn't swing. While we're in
			// the ball (BallCamera owns the camera) we remember its aim each frame; the moment we're set
			// down, hand that aim to PlayerController so it picks up pointing the same way. Caching avoids
			// reading Scene.Camera on the exit frame, when PlayerController may already have overwritten it.
			if ( Carried )
			{
				var cam = Scene.Camera;
				if ( cam.IsValid() ) _lastInBallCamRot = cam.WorldRotation;
			}
			else if ( _wasCarried && _player.IsValid() )
			{
				_player.EyeAngles = _lastInBallCamRot.Angles() with { roll = 0f };
			}

			// Our turn just ended (handed off to the next bowler, or the game finished): put us back in
			// the settee, not stranded up at the lane station where we pressed START. The transform is
			// owner-authoritative, so we move ourselves rather than the host yanking us.
			if ( _wasBowling && !Bowling )
				ReturnToSettee();

			// While we're up, publish our live camera transform so every settee monitor mirrors our view.
			if ( Bowling )
			{
				var cam = Scene.Camera;
				if ( cam.IsValid() )
				{
					BowlCamPos = cam.WorldPosition;
					BowlCamRot = cam.WorldRotation;
				}
			}

			UpdateControlsStation();

			// R: respawn — the escape hatch for a player who's walked off, fallen through the world, or got
			// stuck. Not while riding the ball (that's G to exit) or typing in chat. Goes to the settee of the
			// lane we're SIGNED IN to (on its scorecard), else back to the shared spawn concourse.
			if ( !Bowling && !ChatPanel.IsOpen && Input.Keyboard.Pressed( "R" ) )
				Respawn();
		}
		_wasBowling = Bowling;
		_wasCarried = Carried;
	}

	// R-respawn: to the settee of the lane we're signed in to (JoinedLaneId — i.e. on its scorecard), else
	// back to the shared spawn concourse. Distinct from ReturnToSettee (crane/turn-end), which honours the
	// exact grab spot.
	private void Respawn()
	{
		// Signed in to a lane → drop onto that lane's settee, fanned so returning players don't stack.
		var builder = Lane.ById( Scene, JoinedLaneId )?.Builder;
		if ( builder.IsValid() && builder.SetteeHalfWidthUnits > 0f )
		{
			var half = builder.SetteeHalfWidthUnits;
			var spread = (Math.Abs( Network.Owner?.Id.GetHashCode() ?? 0 ) % 1000) / 1000f; // 0..1, stable
			var y = MathX.Lerp( -half * 0.7f, half * 0.7f, spread );
			GameObject.WorldPosition = builder.SetteeFloorCenter + new Vector3( 0f, y, 0f );
			GameObject.WorldRotation = builder.SetteeFaceRotation;
			return;
		}

		// Not on an active lane → back to the shared spawn area (between the BALL/HELP podiums).
		var spawn = Scene.GetAllComponents<AlleyServer>().FirstOrDefault()?.SpawnArea;
		if ( spawn.IsValid() )
		{
			GameObject.WorldPosition = spawn.WorldPosition;
			GameObject.WorldRotation = spawn.WorldRotation;
		}
	}

	// Drop us back onto the settee carpet, fanned across its width by a stable per-player offset so
	// returning bowlers don't stack on one spot. The lane builder hands us the seating zone's geometry.
	private void ReturnToSettee()
	{
		var builder = ActiveLane?.Builder ?? Lane.Local( Scene )?.Builder;
		if ( !builder.IsValid() ) return;

		// The crane recorded exactly where to set us down (our grab spot, or the settee middle if we were
		// grabbed off it). Prefer that so the drop / R-respawn lands on the same place we were taken from.
		if ( _hasReturnSpot )
		{
			GameObject.WorldPosition = _returnSpot;
			GameObject.WorldRotation = builder.SetteeFaceRotation;
			return;
		}

		var half = builder.SetteeHalfWidthUnits;
		// SetteeHalfWidthUnits is 0 until the lane has actually built its geometry; teleporting then would
		// drop us at the world origin (under the floor → endless fall/respawn, which reads as a hang).
		if ( half <= 0f ) return;
		var spread = (Math.Abs( (Network.Owner?.Id.GetHashCode() ?? 0) ) % 1000) / 1000f; // 0..1, stable
		var y = MathX.Lerp( -half * 0.7f, half * 0.7f, spread );
		GameObject.WorldPosition = builder.SetteeFloorCenter + new Vector3( 0f, y, 0f );
		GameObject.WorldRotation = builder.SetteeFaceRotation; // face down the lane from the settee
	}

	// Find the nearest in-range walk-up station — lane-controls podium OR ball-designer table — and
	// engage/disengage its cursor on E. The two share the E key, so whichever is closer owns the prompt.
	private void UpdateControlsStation()
	{
		// No fiddling with any panel while you're riding the ball.
		if ( Bowling )
		{
			if ( _controlsOpen ) CloseControls();
			if ( _designerOpen ) CloseDesigner();
			if ( _helpOpen ) CloseHelp();
			if ( _teleportOpen ) CloseTeleport();
			_nearStation = _nearDesigner = _nearHelp = _nearTeleport = false;
			return;
		}

		// A panel that's already open owns input until it's closed (E again / Esc / walking out of range).
		if ( _controlsOpen ) { ServiceOpen( NearestStation( out _ ) is not null, CloseControls ); UpdateEngagedLane(); return; }
		if ( _designerOpen ) { ServiceOpen( NearestDesigner( out _ ) is not null, CloseDesigner ); return; }
		if ( _helpOpen ) { ServiceOpen( NearestHelp( out _ ) is not null, CloseHelp ); return; }
		if ( _teleportOpen ) { ServiceOpen( NearestTeleport( out _ ) is not null, CloseTeleport ); return; }

		// Nothing open: pick the nearest of the podiums; it owns the prompt + the E press.
		var lane = NearestStation( out var laneDist );
		var designer = NearestDesigner( out var designerDist );
		var help = NearestHelp( out var helpDist );
		var teleport = NearestTeleport( out var teleportDist );
		if ( lane is not null ) _engagedLane = Lane.Of( lane ); // keep the HUD on this lane even if another podium wins the prompt

		_nearStation = _nearDesigner = _nearHelp = _nearTeleport = false;
		var best = float.MaxValue;
		int kind = 0; // 1 = lane, 2 = designer, 3 = help, 4 = teleport
		if ( lane is not null && laneDist < best ) { best = laneDist; kind = 1; }
		if ( designer is not null && designerDist < best ) { best = designerDist; kind = 2; }
		if ( help is not null && helpDist < best ) { best = helpDist; kind = 3; }
		if ( teleport is not null && teleportDist < best ) { best = teleportDist; kind = 4; }

		_nearStation = kind == 1;
		_nearDesigner = kind == 2;
		_nearHelp = kind == 3;
		_nearTeleport = kind == 4;

		if ( Input.Pressed( "use" ) )
		{
			if ( kind == 1 ) OpenControls();
			else if ( kind == 2 ) OpenDesigner();
			else if ( kind == 3 ) OpenHelp();
			else if ( kind == 4 ) OpenTeleport();
		}
	}

	// Shared open-panel servicing: keep the cursor freed, and close on E / Esc / walking out of range.
	private void ServiceOpen( bool stillInRange, System.Action close )
	{
		if ( Input.Pressed( "use" ) || Input.EscapePressed || !stillInRange )
		{
			Input.EscapePressed = false;
			close();
		}
		else
		{
			Mouse.Visibility = MouseVisibility.Visible; // re-assert in case anything resets it
		}
	}

	// While the lane controls are open, keep _engagedLane / _nearStation pointed at the podium we're at.
	private void UpdateEngagedLane()
	{
		var station = NearestStation( out _ );
		_nearStation = station is not null;
		if ( station is not null ) _engagedLane = Lane.Of( station );
	}

	/// <summary>Close the controls panel from the panel's own CLOSE button (restores look + cursor).</summary>
	public void RequestCloseControls()
	{
		if ( _controlsOpen ) CloseControls();
	}

	/// <summary>Close the ball designer from its own CLOSE button (restores look + cursor).</summary>
	public void RequestCloseDesigner()
	{
		if ( _designerOpen ) CloseDesigner();
	}

	/// <summary>Close the help screen from its own CLOSE button (restores look + cursor).</summary>
	public void RequestCloseHelp()
	{
		if ( _helpOpen ) CloseHelp();
	}

	/// <summary>Close the teleport screen from its own CLOSE button (restores look + cursor).</summary>
	public void RequestCloseTeleport()
	{
		if ( _teleportOpen ) CloseTeleport();
	}

	/// <summary>Teleport (local player) to a lane's settee — the destination the teleport panel's buttons
	/// call. Drops onto the carpet fanned by a stable per-player offset, and closes the panel.</summary>
	public void TeleportToLane( Lane lane )
	{
		var builder = lane?.Builder;
		if ( !builder.IsValid() || builder.SetteeHalfWidthUnits <= 0f ) return;
		var half = builder.SetteeHalfWidthUnits;
		var spread = (Math.Abs( Network.Owner?.Id.GetHashCode() ?? 0 ) % 1000) / 1000f; // 0..1, stable
		var y = MathX.Lerp( -half * 0.7f, half * 0.7f, spread );
		GameObject.WorldPosition = builder.SetteeFloorCenter + new Vector3( 0f, y, 0f );
		GameObject.WorldRotation = builder.SetteeFaceRotation;
		if ( _teleportOpen ) CloseTeleport();
	}

	private LaneStation NearestStation( out float bestDist )
	{
		LaneStation best = null;
		bestDist = float.MaxValue;
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

	private BallDesignerStation NearestDesigner( out float bestDist )
	{
		BallDesignerStation best = null;
		bestDist = float.MaxValue;
		var me = GameObject.WorldPosition;
		foreach ( var s in Scene.GetAllComponents<BallDesignerStation>() )
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

	private HelpStation NearestHelp( out float bestDist )
	{
		HelpStation best = null;
		bestDist = float.MaxValue;
		var me = GameObject.WorldPosition;
		foreach ( var s in Scene.GetAllComponents<HelpStation>() )
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

	private TeleportStation NearestTeleport( out float bestDist )
	{
		TeleportStation best = null;
		bestDist = float.MaxValue;
		var me = GameObject.WorldPosition;
		foreach ( var s in Scene.GetAllComponents<TeleportStation>() )
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

	// Ball designer: same cursor/look handling as the lane controls.
	private void OpenDesigner()
	{
		_designerOpen = true;
		if ( _player.IsValid() ) _player.UseLookControls = false;
		Mouse.Visibility = MouseVisibility.Visible;
	}

	private void CloseDesigner()
	{
		_designerOpen = false;
		if ( _player.IsValid() ) _player.UseLookControls = true;
		Mouse.Visibility = MouseVisibility.Auto;
		// Auto-save on close: persist whatever the (already-live-applied) design currently is.
		if ( !IsProxy ) Design.Save();
	}

	// How-to-play screen: same cursor/look handling as the other podiums (read-only, nothing to save).
	private void OpenHelp()
	{
		_helpOpen = true;
		if ( _player.IsValid() ) _player.UseLookControls = false;
		Mouse.Visibility = MouseVisibility.Visible;
	}

	private void CloseHelp()
	{
		_helpOpen = false;
		if ( _player.IsValid() ) _player.UseLookControls = true;
		Mouse.Visibility = MouseVisibility.Auto;
	}

	// Lane-teleport screen: same cursor/look handling as the other podiums.
	private void OpenTeleport()
	{
		_teleportOpen = true;
		if ( _player.IsValid() ) _player.UseLookControls = false;
		Mouse.Visibility = MouseVisibility.Visible;
	}

	private void CloseTeleport()
	{
		_teleportOpen = false;
		if ( _player.IsValid() ) _player.UseLookControls = true;
		Mouse.Visibility = MouseVisibility.Auto;
	}

	// Never leave the cursor freed / look disabled if our avatar goes away while engaged.
	protected override void OnDisabled()
	{
		if ( _controlsOpen ) CloseControls();
		if ( _designerOpen ) CloseDesigner();
		if ( _helpOpen ) CloseHelp();
		if ( _teleportOpen ) CloseTeleport();
	}

	protected override void OnDestroy()
	{
		if ( _controlsOpen ) CloseControls();
		if ( _designerOpen ) CloseDesigner();
		if ( _helpOpen ) CloseHelp();
		if ( _teleportOpen ) CloseTeleport();
		_dressCts?.Cancel();
	}

	/// <summary>Bowler → director: a settled ball knocked <paramref name="knocked"/> pins. Routed to the
	/// lane's director-OWNER (an [Rpc.Owner] on the director, no longer necessarily the host) which books it
	/// onto the scorecard right away. Rotating to the next bowler is a separate step — see <see cref="EndTurn"/>.</summary>
	public void ReportBall( int knocked, int downMask, bool pocket )
		=> Director?.BookBall( knocked, downMask, pocket );

	/// <summary>Bowler → director: our turn is fully over — the dropoff crane has set us back on the settee,
	/// so the director can now rotate to the next bowler (which reclaims + re-racks the ball).</summary>
	public void EndTurn()
		=> Director?.EndTurnRpc();

	/// <summary>Bowler → director: escape hatch — let me out of the ball. The director ends our turn so the
	/// game moves on (and we drop back to the settee). For unsticking a turn that's wedged mid-flight.</summary>
	public void RequestLeaveBall()
		=> Director?.LeaveBallRpc();

}
