using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Runs a single throw's lifecycle and scores the pins, the way a modern alley's pin-detection
/// camera does: a pin counts as DOWN once it's either tipped past <see cref="TiltThresholdDegrees"/>
/// from vertical OR shoved off its spot by more than <see cref="DisplacementThresholdInches"/> — a
/// pin merely nudged but still standing on its spot stays UP.
///
/// Flow:
///   Rolling  — the player drives the ball normally.
///   (ball centre crosses the lane's throw line, a few feet short of the pins)
///   Watching — the ball's input AND braking are cut so it coasts on momentum into the pins, and
///              the camera pulls back/up for an overview of the hit. Held for <see cref="WatchSeconds"/>.
///   (reset)  — pins are re-racked on their spots, the ball is teleported back to the run-up and
///              handed back to the player, and we return to Rolling.
///
/// <see cref="PinStatuses"/> and <see cref="State"/> are read live by the top-left overlay panel.
/// </summary>
public sealed class GameDirector : Component
{
	// --- Director ownership (claimable per-lane authority) ------------------------------
	// The director used to be host-authoritative. Now each lane's director is a CLAIMABLE, network-owned
	// authority: the player who presses START takes ownership of this (networked) director object and runs
	// the whole game for the lane — scoring, turn rotation, racking — on their machine; the synced fields
	// below (plain [Sync], owner→everyone) mirror it to every client. Ownership is relinquished back to the
	// host at the end of the game (STOP / game complete), so the lane returns to the lobby free to be
	// claimed again. Until claimed the host owns it (it runs the lobby), so solo/host play is unchanged.
	//
	// "Am I the authority for this lane?" — true if I own the director object (or there's no live session,
	// e.g. the editor). Everything that used to gate on Networking.IsHost now gates on this instead.
	public bool IsDirector => !Networking.IsActive || !GameObject.Network.Active || GameObject.Network.IsOwner;

	/// <summary>UI-facing alias: does the LOCAL client run this lane's game (show STOP / crane toggle)?</summary>
	public bool LocalIsDirector => IsDirector;

	private bool _dirNetReady; // host: have we tagged the director object Takeover yet?

	// Take ownership of this lane's director so OUR writes to the synced state are authoritative. Called
	// when a player claims the lane (START). No-op in the editor / if we already own it.
	private void ClaimDirector()
	{
		if ( Networking.IsActive && GameObject.Network.Active && !GameObject.Network.IsOwner )
			GameObject.Network.TakeOwnership();
	}

	// Hand the director back to the host at the end of a game, so the lane's lobby is host-run again and
	// the next player can claim it. Host keeps its own directors (nothing to drop).
	private void RelinquishDirector()
	{
		if ( Networking.IsActive && GameObject.Network.Active && GameObject.Network.IsOwner && !Networking.IsHost )
			GameObject.Network.DropOwnership();
	}


	/// <summary>Hard CAP (seconds) on the watch view before re-racking. We normally cut it short the
	/// moment the action settles (see the Settle* knobs); this is just the backstop for the rare case
	/// where something jitters forever.</summary>
	[Property] public float WatchSeconds { get; set; } = 5f;

	/// <summary>End the watch early once the ball and every standing pin have been quieter than the
	/// Settle thresholds for this long (seconds). Short, so a clean rack snaps back near-instantly.</summary>
	[Property, Group( "Settle" )] public float SettleQuietSeconds { get; set; } = 0.35f;
	/// <summary>Never settle early before at least this long has passed — cheap insurance against a
	/// one-frame fluke at the instant the watch arms.</summary>
	[Property, Group( "Settle" )] public float MinWatchSeconds { get; set; } = 0.3f;
	/// <summary>A body counts as "still" below this linear speed (u/s). Pins cascade at hundreds–
	/// thousands of u/s, a settled one is ~0, so there's a wide margin.</summary>
	[Property, Group( "Settle" )] public float SettleSpeedThreshold { get; set; } = 25f;
	/// <summary>And below this angular speed (rad/s) — catches a pin still spinning in place.</summary>
	[Property, Group( "Settle" )] public float SettleAngularThreshold { get; set; } = 1.0f;

	/// <summary>Soft cap on how many bowlers a single game seats. Tunable per-lane on the director in the
	/// scene. Joiners past this are simply not locked into the roster when START is pressed.</summary>
	[Property, Group( "Players" )] public int MaxPlayers { get; set; } = 6;

	/// <summary>Physics substeps for the world. Re-asserted every frame because the engine replaces
	/// the PhysicsWorld after OnStart, so setting it once didn't stick (the probe was reading 1).
	/// Resolves the fast 10-pin cascade instead of smearing the ball through it in coarse steps.
	/// With the physics tick now at 120 Hz (Physics.config FixedUpdateFrequency), 3 substeps already
	/// gives ~360 solve steps/s — keep it low here or you'll multiply into thousands per second.</summary>
	[Property] public int PhysicsSubSteps { get; set; } = 3;

	/// <summary>Spew the per-throw pin-crash diagnostics to the console (impact/exit speeds, the physics
	/// probe, and the per-pin leave breakdown). Off by default — flip it on in the inspector when tuning
	/// the crash.</summary>
	[Property, Group( "Debug" )] public bool LogPinCrash { get; set; } = false;

	/// <summary>Log the pin-AUDIO pipeline (every contact + play/skip decision) — separate from
	/// <see cref="LogPinCrash"/> so the sound debug isn't buried under the per-frame coast spam.</summary>
	[Property, Group( "Debug" )] public bool LogPinAudio { get; set; } = false;

	/// <summary>Tip past this many degrees off vertical and the pin reads as knocked down.</summary>
	[Property] public float TiltThresholdDegrees { get; set; } = 30f;
	/// <summary>Shoved this many real inches off its spot (horizontally) and the pin reads as down,
	/// even if it's somehow still upright.</summary>
	[Property] public float DisplacementThresholdInches { get; set; } = 4f;

	// Spent = the throw has settled and been booked; we're idle until the next ball/turn is set up (so the
	// pit-resting ball never re-arms watch-mode). Craning = the claw is flying the ball.
	public enum RoundState { Rolling, Watching, Craning, Spent }
	public RoundState State { get; private set; } = RoundState.Rolling;

	/// <summary>Top travel speed (u/s) the claw flies at — a CAP, so a long full-lane trip just takes
	/// longer rather than blurring across in a fixed time. Duration of a leg = distance ÷ this (floored by
	/// <see cref="CraneMinTravelSeconds"/>). The lane is ~22k units end to settee, so this is what keeps
	/// the transport readable instead of a snap.</summary>
	[Property, Group( "Crane" )] public float CraneSpeed { get; set; } = 12700f;
	/// <summary>Floor on a travel leg's duration (seconds) so a short hop (seat→seat) isn't instant.</summary>
	[Property, Group( "Crane" )] public float CraneMinTravelSeconds { get; set; } = 0.45f;
	/// <summary>How far (units) the ball floats straight UP out of its rest spot before setting off.</summary>
	[Property, Group( "Crane" )] public float CraneLiftHeight { get; set; } = 500f;
	/// <summary>Seconds for that float-up.</summary>
	[Property, Group( "Crane" )] public float CraneLiftSeconds { get; set; } = 0.5f;
	/// <summary>Beat (seconds) the ball hovers at the top before it starts travelling.</summary>
	[Property, Group( "Crane" )] public float CraneHoverSeconds { get; set; } = 0.4f;
	/// <summary>How high (units) above a player the claw hovers before it sinks to scoop them (and rises
	/// back to after a scoop/drop).</summary>
	[Property, Group( "Crane" )] public float CraneAboveHeight { get; set; } = 230f;
	/// <summary>The noticeable beat (seconds) the ball holds directly above a player both before sinking
	/// onto them and after rising back off — the claw-game "lining up / lifting clear" pause.</summary>
	[Property, Group( "Crane" )] public float CraneAbovePauseSeconds { get; set; } = 0.55f;
	/// <summary>Seconds for the vertical sink onto a player (and the matching rise back up).</summary>
	[Property, Group( "Crane" )] public float CraneSinkSeconds { get; set; } = 0.6f;
	/// <summary>Extra height (units) the travel bulges up mid-leg, on top of the float-up, so the claw
	/// sails well OVER the rack on its way to the settee behind the pins.</summary>
	[Property, Group( "Crane" )] public float CraneArcHeight { get; set; } = 260f;

	// --- Multiplayer ownership ----------------------------------------------------------
	// The director is host-authoritative. Exactly one connected player is "up" at a time; the host
	// publishes their connection id here and every client compares it against its own to decide
	// whether IT drives the ball + ball camera (vs. walking its Terry around the settee). Empty =
	// nobody up (lobby / between turns), so the ball sits parked and everyone just walks.
	[Sync] public Guid ActiveBowlerId { get; set; }

	/// <summary>Bumped by the host every time a turn is (re)assigned — including when the SAME bowler is
	/// up again (solo play, or two turns in a row). The bowler client begins a fresh turn whenever this
	/// changes, so back-to-back turns by one player still re-rack and re-roll instead of hanging on the
	/// parked ball. (Watching ActiveBowlerId alone can't catch a same-bowler hand-back — it never edges.)</summary>
	[Sync] public int TurnSerial { get; set; }

	/// <summary>True if the local client is the bowler currently up. (With no active network session
	/// — e.g. running the scene raw in the editor before a lobby exists — defaults to true so the
	/// ball is still drivable.)</summary>
	public bool LocalIsBowling =>
		!Networking.IsActive ? true : (ActiveBowlerId != Guid.Empty && ActiveBowlerId == Connection.Local.Id);

	// --- Shared, host-authoritative game state (synced to every client) -----------------
	// The host runs all the scoring (against its _players + pins); these mirror just enough for any
	// client's scoreboard/console to render the same thing. Strings keep it simple: the per-player
	// frame data is encoded "balls,balls;balls;…" and decoded back in BuildScoreView.

	public enum GamePhase { Lobby, Playing }
	/// <summary>Lobby = players join/leave; Playing = a game is on (START pressed). Host-set.</summary>
	[Sync] public GamePhase Phase { get; set; } = GamePhase.Lobby;
	/// <summary>Host toggle (lane controls): when off, the ball is teleported between spots instead of
	/// flying the choreographed claw animation — the scoop/drop body↔ball swaps and the roll still
	/// happen, just instantly. Synced so the active bowler (who runs the crane) reads the host's choice.</summary>
	[Sync] public bool CraneEnabled { get; set; } = true;
	/// <summary>The bowlers in this game, in turn order (only meaningful in Playing).</summary>
	[Sync] public NetList<string> RosterNames { get; set; } = new();
	/// <summary>Per-roster-player encoded frame data, parallel to <see cref="RosterNames"/>.</summary>
	[Sync] public NetList<string> RosterScores { get; set; } = new();
	/// <summary>Per-roster-player split markers, parallel to <see cref="RosterNames"/>: a comma-joined
	/// list of "frame.ball" tokens (the scoreboard circles those ball boxes). Splits are detected on the
	/// bowler's leave; this surfaces them so every client circles the same counts.</summary>
	[Sync] public NetList<string> RosterSplits { get; set; } = new();
	/// <summary>Whose turn it is (index into the roster).</summary>
	[Sync] public int CurrentPlayer { get; set; }
	/// <summary>The frame everyone's on (0–9).</summary>
	[Sync] public int CurrentFrame { get; set; }
	/// <summary>True once the final frame of the last bowler is complete.</summary>
	[Sync] public bool GameComplete { get; set; }

	/// <summary>A pin's number (1–10) and whether it currently reads as knocked down.</summary>
	public readonly record struct PinStatus( int Number, bool Down );

	private readonly List<PinStatus> _statuses = new();   // bowler-local scoring scratch (from Score())

	/// <summary>Bit n (1–10) set = pin n reads as down. The single per-client source every pin readout
	/// draws from: the active bowler fills it from its authoritative physics (<see cref="Score"/>), and
	/// every spectator derives it from the replicated rack (<see cref="EvaluateDownBits"/>), so all four
	/// pin surfaces agree on every machine even though only the bowler simulates the pins.</summary>
	public int PinDownBits { get; private set; }

	/// <summary>Live per-pin standing/down state, ordered by pin number, derived from the bitmask.</summary>
	public IReadOnlyList<PinStatus> PinStatuses
	{
		get
		{
			var list = new List<PinStatus>( 10 );
			for ( var n = 1; n <= 10; n++ ) list.Add( new PinStatus( n, IsPinDown( n ) ) );
			return list;
		}
	}

	/// <summary>Count of pins currently down — handy for the overlay header / build hash.</summary>
	public int DownCount
	{
		get { var c = 0; for ( var n = 1; n <= 10; n++ ) if ( IsPinDown( n ) ) c++; return c; }
	}

	/// <summary>The rack laid out as the bowler sees it down-lane: head pin (1) nearest at the BOTTOM,
	/// the back row (7–10) farthest at the top, mirrored left-right (10 on the left) — a 180° turn of
	/// the physical rack. The single source every pin readout (the corner overlay AND the big
	/// lane-width board above the backstop) draws from, so they can never disagree.</summary>
	public static readonly int[][] RackRows =
	{
		new[] { 10, 9, 8, 7 },
		new[] { 6, 5, 4 },
		new[] { 3, 2 },
		new[] { 1 },
	};

	/// <summary>Is pin <paramref name="number"/> (1–10) currently reading as down?</summary>
	public bool IsPinDown( int number ) => (PinDownBits & (1 << number)) != 0;

	/// <summary>The current announcer call-out text the lane banner shows (e.g. "STRIKE!", "SPLIT!", "6").
	/// Set locally by <see cref="Announce"/> on every client; not synced (the panel is lane-local).</summary>
	public string CalloutText { get; private set; }
	/// <summary>When <see cref="CalloutText"/> was set (<see cref="Time.Now"/>), so the banner can fade out.</summary>
	public float CalloutAt { get; private set; }

	private LaneBuilder _lane;
	private BallController _ball;
	private BallCamera _camera;

	// --- Approach-B throw networking (the active bowler is authoritative for their own turn) -------
	private readonly List<int> _localBalls = new(); // bowler-side: the balls thrown THIS frame, for the rules
	private int _servedTurn = -1;    // the TurnSerial we last began a turn for (-1 = none yet this client)
	private bool _ballNetReady;      // host: have we tagged the networked ball Takeover yet?
	private TerryAvatar _localAvatar;
	private TerryAvatar LocalAvatar =>
		(_localAvatar.IsValid() ? _localAvatar : (_localAvatar = Scene.GetAllComponents<TerryAvatar>().FirstOrDefault( a => a.IsValid() && !a.IsProxy )));

	// --- Crane (claw) transport state (bowler-side) ------------------------------------
	// The active bowler flies the ball kinematically to fetch/return their player, always starting from
	// wherever the ball currently sits (it's never reset to the start between turns):
	//   Pickup  — fly to the bowler's seat, scoop them IN, then fly to the lane start to roll (2 legs).
	//   Return  — already in the ball (solo, or a 2nd/3rd ball): fly straight up out of the pit to the
	//             lane start and roll (1 leg). This is the "ball return".
	//   Dropoff — fly to the bowler's seat and set them down; the ball stays there for the next pickup to
	//             fly on from (1 leg). Only between DIFFERENT bowlers.
	// A crane trip is a scripted list of steps (float up, hover, travel, pause above, sink, scoop, …) run in
	// order — see BuildCraneSteps for each job's choreography.
	private enum CraneJob { None, Pickup, Return, Dropoff }
	private enum CraneStepKind
	{
		Move,    // vertical/short lerp to Pos over Seconds (lift, sink, rise)
		Travel,  // lerp to Pos at the capped speed, with a rack-clearing bulge
		Wait,    // hold position for Seconds (hover / above-pause). Track ⟹ hover OVER the live player.
		Descend, // sink onto the LIVE player, chasing them; ends (→ Scoop) only once the ball is on them
		Rise,    // vertical lift straight up from wherever the ball is now (post-scoop)
		Snap,    // instant: teleport ball to Pos (crane-disabled: skips the animated travel)
		Scoop,   // instant: body → ball
		Drop,    // instant: ball → body at the seat
		Roll,    // instant: end the trip, restore physics, roll
		EndDrop, // instant: end the trip parked frozen at the seat, hand off to the host
	}
	private struct CraneStep
	{
		public CraneStepKind Kind;
		public Vector3 Pos;
		public float Seconds;
		public bool Track; // Travel/Wait: home in on the bowler's LIVE position instead of a fixed Pos
	}
	private CraneJob _craneJob;
	private bool _ballCraned;     // ball currently in kinematic crane mode (collider on ⟺ rolling)
	// After a throw we keep the camera on the pin-deck watch view while the pins fade/reset and the ball
	// floats up out of the pit; only once the ball tops out at its lift apex do we release the camera onto
	// it (so it eases on at the top and follows it back, instead of swinging down to the ball in the pit).
	private bool _releaseWatchAtApex;
	private readonly List<CraneStep> _craneSteps = new();
	private int _craneStep;
	private float _craneStepElapsed, _craneStepDuration;
	private Vector3 _craneStepFrom, _craneStepTo;
	private Vector3 _grabPos;     // where the claw grabbed us this turn
	private bool _grabOnSettee;   // ...and whether that was on the settee (else we drop at the middle)
	private Vector3 _dropSpot;    // where to set us back down at turn's end

	private Vector3 _ballHome;
	private Rotation _ballHomeRot;
	private float _watchStart;
	private float _quietSince;  // when the scene first went still this watch (-1 = currently moving)
	private bool _probed;      // one-shot physics probe logged?
	private float _impactSpeed; // ball speed at the throw line, to compare against the deck-exit speed
	private bool _exitLogged;   // logged the deck-exit speed for this throw?
	private float _peakPinSpin; // peak pin angular speed seen this throw (rad/s)
	private float _peakPinVel;  // peak pin linear speed seen this throw (u/s)

	// --- Game / frame state -------------------------------------------------------------
	// A bowling game is 10 frames of 1–2 balls (the 10th gets a 3rd on a strike/spare). The host
	// keeps one authoritative PlayerScore per bowler in the roster; bowlers alternate within a
	// shared frame (P1 frame1, P2 frame1, … then everyone steps to frame2). AdvanceTurn rotates the
	// active bowler and steps the frame once the last bowler is done. This state is host-only and
	// mirrored to clients through the synced Roster* fields (see PublishScores).

	/// <summary>One player's scratch: the pin counts knocked on each ball, grouped by frame.</summary>
	public sealed class PlayerScore
	{
		public string Name { get; set; }
		/// <summary>Ten frames; each holds the pins felled per ball (1–2 balls, or 3 in the 10th).</summary>
		public readonly List<List<int>> Frames = new();
		/// <summary>Which (frame, ball-index) leaves were splits — host-side, published via RosterSplits.</summary>
		public readonly HashSet<(int frame, int ball)> Splits = new();
		public PlayerScore( string name )
		{
			Name = name;
			for ( var i = 0; i < 10; i++ ) Frames.Add( new List<int>() );
		}
	}

	private readonly List<PlayerScore> _players = new();   // host-authoritative scoring state
	private readonly List<Guid> _rosterIds = new();        // host: roster slot -> owning connection id
	private int _current;            // whose turn (index into _players)
	private int _frame;              // current frame 0..9 (shared; players alternate within it)
	private int _ballStartDown;      // pins already down entering the current ball (swept from earlier balls this frame)
	private readonly HashSet<int> _sweptPins = new(); // pins cleared off the deck this frame (down on an earlier ball)
	private bool _gameOver;

	/// <summary>True once the final frame is complete — panels show GAME OVER. Mirrors the synced
	/// <see cref="GameComplete"/> so every client reads the same flag.</summary>
	public bool GameOver => GameComplete;

	protected override void OnStart()
	{
		// The lobby is auto-created once by AlleyServer (a single component) — not here: with N lane
		// directors, N calls would race to create it.

		// Resolve our own lane's parts (siblings under our shared Lane root), never a scene-global scan —
		// each director runs only its own lane's geometry/ball/camera.
		var lane = Lane.Of( this );
		_lane = lane?.Builder;
		_ball = lane?.Ball;
		_camera = lane?.Camera;

		if ( _ball.IsValid() )
		{
			// Rack the ball in the run-up area on the lane platform (sitting on the surface), not at
			// the world origin. Fall back to wherever it was placed if the lane isn't up yet.
			_ballHome = _lane.IsValid()
				? _lane.RunUpWorldPosition + Vector3.Up * _ball.Radius
				: _ball.WorldPosition;
			_ballHomeRot = _ball.WorldRotation;

			// Put it there now, too, so the first throw starts from the run-up under the camera.
			PlaceBallHome();
		}

		// Start in the LOBBY: no roster yet. Players walk up and Join from the lane controls; the
		// host presses START to lock the roster in and begin frame 1. (Host publishes the empty
		// state so joining clients render a clean board.)
		_players.Clear();
		_rosterIds.Clear();
		_current = 0;
		_frame = 0;
		_ballStartDown = 0;
		_sweptPins.Clear();
		_gameOver = false;
		if ( IsDirector )
		{
			Phase = GamePhase.Lobby;
			PublishScores();
		}
	}

	// The on-screen HUD is built by LocalHudSystem (a purely-local GameObjectSystem), not here — it's a
	// client-local, non-networked thing that belongs to the player, not to any director.
	//
	// Avatar spawn/despawn is NOT here any more — with N lanes, N directors would each spawn a Terry per
	// joiner. It's a single server-level responsibility now: see AlleyServer (one INetworkListener on the
	// scene root that spawns exactly one walking Terry per connection).

	// --- Lobby controls (driven by the lane controls panel) -----------------------------
	// Join/Leave is each client toggling its OWN avatar's (owner-controlled) Joined flag directly — no RPC.
	// START claims this lane's director (takes ownership) and runs the game on the presser's machine; STOP
	// relinquishes it back to the host. Both are called straight on the director from the local controls
	// panel, so there's no instance-RPC — the presser IS the new authority.

	/// <summary>Claim this lane and lock the joined players into a fresh scorecard, tipping off frame 1.
	/// Any player at the lobby can press START — it takes ownership of the director so THEY run the game.</summary>
	public void StartGame()
	{
		if ( Phase == GamePhase.Playing ) return;
		ClaimDirector();               // become this lane's authority before we write any synced state
		if ( !IsDirector ) return;     // ownership didn't take (shouldn't happen with Takeover) — bail

		// Only roster players who joined THIS lane (each lane runs its own game), so with N lanes a START
		// on one alley doesn't sweep up everyone who's joined elsewhere.
		var laneId = Lane.Of( this )?.GameObject.Id ?? Guid.Empty;
		var joined = Scene.GetAllComponents<TerryAvatar>()
			.Where( a => a.IsValid() && a.Joined && a.JoinedLaneId == laneId )
			.Take( Math.Max( 1, MaxPlayers ) ) // soft cap — extra joiners just don't make this game's roster
			.ToList();
		if ( joined.Count == 0 ) return; // nobody to play

		_players.Clear();
		_rosterIds.Clear();
		foreach ( var a in joined )
		{
			_players.Add( new PlayerScore( a.DisplayName ) );
			_rosterIds.Add( a.GameObject.Network.Owner?.Id ?? Guid.Empty );
		}

		_current = 0;
		_frame = 0;
		_ballStartDown = 0;
		_sweptPins.Clear();
		_gameOver = false;
		Phase = GamePhase.Playing;

		// Host owns the rack in the gap: present a clean, settled deck BEFORE spotlighting the first
		// bowler, so they grab an already-good rack (no contested respot). They roll on their own machine
		// (BeginTurn) the moment their spotlight (ActiveBowlerId) lands. Snap it (instant) — the game's just
		// starting, there's no throw to watch the rack glide in from.
		HostRackForNextBowler( instant: true );
		SetActiveBowler();
		PublishScores();
		Log.Info( $"[terryball][game] START — {_players.Count} bowler(s), frame 1" );
	}

	/// <summary>Director: clear the scorecard, drop everyone back to the lobby (walking the settee), and
	/// relinquish the lane back to the host so it can be claimed again.</summary>
	public void StopGame()
	{
		if ( !IsDirector ) return;

		Phase = GamePhase.Lobby;
		_players.Clear();
		_rosterIds.Clear();
		_current = 0;
		_frame = 0;
		_gameOver = false;
		ActiveBowlerId = Guid.Empty; // clears everyone's derived TerryAvatar.Bowling
		State = RoundState.Rolling;

		if ( _camera.IsValid() ) _camera.Watching = false;
		HostReclaimThrowOwnership(); // take the ball + rack back before we re-rack/park them
		FullRack( instant: true );   // straight back to a clean deck — we're dropping to the lobby
		PlaceBallHome();
		PublishScores();
		RelinquishDirector();        // hand the lane back to the host lobby
		Log.Info( "[terryball][game] STOP — back to the lobby" );
	}

	/// <summary>Point the spotlight at the current roster player by publishing their connection id — every
	/// avatar's <see cref="TerryAvatar.Bowling"/> is DERIVED from this, so there's nothing per-avatar to
	/// write (a non-host director can't write another player's field anyway).</summary>
	private void SetActiveBowler()
	{
		TurnSerial++; // a new turn is starting — even if it's the same bowler as last turn (see TurnSerial)
		ActiveBowlerId = (_current >= 0 && _current < _rosterIds.Count) ? _rosterIds[_current] : Guid.Empty;
	}

	/// <summary>Director: mirror the authoritative scoring state into the synced fields every client
	/// renders from. Called after anything that changes the cards or whose turn it is.</summary>
	private void PublishScores()
	{
		if ( !IsDirector ) return;

		RosterNames.Clear();
		RosterScores.Clear();
		RosterSplits.Clear();
		foreach ( var p in _players )
		{
			RosterNames.Add( p.Name );
			RosterScores.Add( EncodeFrames( p.Frames ) );
			RosterSplits.Add( string.Join( ",", p.Splits.Select( s => $"{s.frame}.{s.ball}" ) ) );
		}
		CurrentPlayer = _current;
		CurrentFrame = _frame;
		GameComplete = _gameOver;
	}

	// Frames encode as "b,b;b;…" — balls comma-separated within a frame, frames semicolon-separated.
	private static string EncodeFrames( List<List<int>> frames )
		=> string.Join( ";", frames.Select( f => string.Join( ",", f ) ) );

	private static List<List<int>> DecodeFrames( string s )
	{
		var result = new List<List<int>>();
		if ( !string.IsNullOrEmpty( s ) )
		{
			foreach ( var fr in s.Split( ';' ) )
			{
				var balls = new List<int>();
				if ( fr.Length > 0 )
					foreach ( var b in fr.Split( ',' ) )
						if ( int.TryParse( b, out var v ) ) balls.Add( v );
				result.Add( balls );
			}
		}
		while ( result.Count < 10 ) result.Add( new List<int>() );
		return result;
	}

	// Splits encode as "frame.ball,frame.ball,…" — decode to per-frame sets of ball-box indices to circle.
	private static Dictionary<int, HashSet<int>> DecodeSplits( string s )
	{
		var result = new Dictionary<int, HashSet<int>>();
		if ( string.IsNullOrEmpty( s ) ) return result;
		foreach ( var tok in s.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var fb = tok.Split( '.' );
			if ( fb.Length == 2 && int.TryParse( fb[0], out var f ) && int.TryParse( fb[1], out var b ) )
			{
				if ( !result.TryGetValue( f, out var set ) ) result[f] = set = new();
				set.Add( b );
			}
		}
		return result;
	}

	protected override void OnUpdate()
	{
		if ( !_lane.IsValid() || !_ball.IsValid() ) return;

		// Re-assert every frame: the engine swaps the PhysicsWorld after OnStart, so a one-time set
		// silently reverted to 1. This keeps the cascade properly substepped on the live world.
		Scene.PhysicsWorld.SubSteps = PhysicsSubSteps;

		// Host: bring the shared, networked rack online (once the lane is built) and flag the ball AND the
		// director Takeover-transferable — the bowler grabs the ball/rack for their turn, and any player
		// can claim the director to run the lane. World setup, independent of who's the director. Idempotent.
		if ( Networking.IsHost )
		{
			_lane.HostSpawnPins();
			if ( !_ballNetReady && _ball.GameObject.Network.Active )
			{
				_ball.GameObject.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
				_ballNetReady = true;
			}
			if ( !_dirNetReady && GameObject.Network.Active )
			{
				GameObject.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
				_dirNetReady = true;
			}
		}

		// Host keyboard P was a quick START/STOP toggle for solo testing. Commented out for now.
		// if ( Networking.IsHost && Input.Keyboard.Pressed( "P" ) )
		// {
		// 	if ( Phase == GamePhase.Playing ) StopGame(); else StartGame();
		// 	return;
		// }

		if ( Phase != GamePhase.Playing )
		{
			// STOP / disconnect / game-end: restore the ball's physics if a crane left it kinematic.
			if ( _craneJob != CraneJob.None || _ballCraned ) AbortCrane();
			return;
		}

		// Director-side housekeeping (only the lane's authority runs this).
		if ( IsDirector )
		{
			// Self-heal: if we just inherited a "Playing" game with no roster — e.g. the previous director
			// disconnected and ownership reverted to us (the host) — there's no authoritative state left to
			// run, so drop the lane back to the lobby cleanly rather than wedging.
			if ( _players.Count == 0 )
			{
				StopGame();
				return;
			}

			// If the bowler who's UP has left the session, their orphaned rack would sit forever. Advance
			// the turn so we reclaim the ball + rack and the next bowler is up. (This used to live in the
			// host's OnDisconnected; the director may not be the host now, so it's polled here instead.)
			if ( ActiveBowlerId != Guid.Empty && !GameComplete
				&& !Networking.Connections.Any( c => c.Id == ActiveBowlerId ) )
			{
				Log.Info( "[terryball][game] active bowler left — advancing the turn" );
				AdvanceTurn();
			}
		}

		// Whoever is UP owns the lane's one shared networked ball + rack and runs the whole throw on
		// their machine; the engine replicates the transforms to everyone else (spectators), so there's
		// no hand-rolled stream — the bowler just plays, the watchers just watch.
		var bowling = LocalIsBowling;

		// Escape hatch: G lets the bowler bail out of the ball (the HUD shows a button too). Works even
		// if the turn's wedged — it RPCs the host to end our turn. Skip while the chat box has the keys.
		if ( bowling && !ChatPanel.IsOpen && Input.Keyboard.Pressed( "G" ) )
			LocalAvatar?.RequestLeaveBall();

		// Begin a fresh turn whenever the host hands US a NEW turn serial (covers the first turn AND a
		// same-bowler hand-back, which the old become-bowler edge silently missed — that was the hang).
		if ( bowling && _servedTurn != TurnSerial )
		{
			_servedTurn = TurnSerial;
			BeginTurn();     // we just got the ball — rack + reset
		}

		if ( !bowling )
		{
			// We just stopped being the bowler mid-FLIGHT (G-bail / the turn was yanked): abort so the ball
			// isn't left collider-off. (A clean end-of-turn leaves _craneJob None — a Dropoff parks the ball
			// frozen on purpose, which is fine on this now-spectating client.)
			if ( _craneJob != CraneJob.None ) AbortCrane();

			// Spectator: the bowler owns the one shared rack + ball, so the engine replicates them to
			// us automatically. We just derive the live down-mask off those replicated pins so our pin
			// panels read the same leave as the bowler's.
			PinDownBits = EvaluateDownBits();
			return;
		}

		// We're the bowler: we own the rack + ball, so live physics here replicates to everyone.
		Probe();
		Score();

		switch ( State )
		{
			case RoundState.Craning:
				// Flying the ball to fetch us (pickup) or set us back down (dropoff). No input, no scoring.
				UpdateCrane();
				break;

			case RoundState.Rolling:
				// Arm watch-mode the moment the ball's centre passes the throw line (unless the
				// game's already over and the ball is parked). GameComplete is synced, so this is
				// correct whether or not the bowler is the host.
				if ( !GameComplete && _ball.WorldPosition.x >= _lane.ThrowLineWorldX )
					EnterWatch();
				break;

			case RoundState.Watching:
				// Walk the pins once: track peak motion (debug) AND whether the whole rack is still.
				var pinsStill = true;
				foreach ( var p in _lane.Pins )
				{
					var prb = p.Object.IsValid() && p.Object.Enabled ? p.Object.Components.Get<Rigidbody>() : null;
					if ( !prb.IsValid() ) continue; // swept/disabled pins don't count
					var spin = prb.AngularVelocity.Length;
					var vel = prb.Velocity.Length;
					_peakPinSpin = MathF.Max( _peakPinSpin, spin );
					_peakPinVel = MathF.Max( _peakPinVel, vel );
					if ( vel > SettleSpeedThreshold || spin > SettleAngularThreshold )
						pinsStill = false;
				}

				// DEBUG: sample the ball's speed the moment it clears the back of the rack — how much
				// momentum did 10 pins actually take out of it? (God-mode if it keeps most of it.)
				if ( LogPinCrash && !_exitLogged && _ball.WorldPosition.x >= _lane.PinDeckEndWorldX )
				{
					_exitLogged = true;
					var bb = _ball.Components.Get<Rigidbody>();
					var exit = bb.IsValid() ? bb.Velocity.Length : 0f;
					var retained = _impactSpeed > 1f ? exit / _impactSpeed * 100f : 0f;
					Log.Info( $"[terryball][exit] cleared the rack: speed={exit:F0} u/s  " +
						$"retained={retained:F0}% of impact({_impactSpeed:F0})  downSoFar={DownCount}  " +
						$"peakPinSpin={_peakPinSpin:F1} rad/s  peakPinVel={_peakPinVel:F0} u/s" );
				}
				// The ball must also be still — rolled to rest in the pit, or stopped/deflected on the
				// lane. This is the guard against settling before impact: while the ball is coasting in
				// it's fast, so the scene is never "quiet" until the throw has actually played out.
				var ballBody = _ball.Components.Get<Rigidbody>();
				var ballStill = ballBody.IsValid() &&
					ballBody.Velocity.Length <= SettleSpeedThreshold && ballBody.AngularVelocity.Length <= SettleAngularThreshold;
				// Once the ball has driven THROUGH the rack it can't change the leave any more, so don't
				// wait on it rattling around the pit — the pins being still is enough. Only a ball that
				// stalled short of the deck (gutter/weak roll) still has to come to rest itself.
				var ballPastDeck = _ball.WorldPosition.x >= _lane.PinDeckEndWorldX;

				if ( pinsStill && (ballPastDeck || ballStill) )
				{
					if ( _quietSince < 0f ) _quietSince = Time.Now;
				}
				else
				{
					_quietSince = -1f;
				}

				var watched = Time.Now - _watchStart;
				var settledEarly = _quietSince >= 0f && watched >= MinWatchSeconds &&
					Time.Now - _quietSince >= SettleQuietSeconds;

				if ( settledEarly || watched >= WatchSeconds )
					ProcessSettledBall();
				break;
		}
	}

	private void EnterWatch()
	{
		State = RoundState.Watching;
		_watchStart = Time.Now;
		_quietSince = -1f;                  // not yet still (ball is coasting in fast)
		_ball.ControlsLocked = true;        // ignore input + braking: coast into the pins
		if ( _camera.IsValid() ) _camera.Watching = true; // back off and up
		_exitLogged = false;
		_peakPinSpin = 0f;
		_peakPinVel = 0f;

		// DEBUG: ball state the instant it crosses the throw line (impact conditions).
		var bb = _ball.Components.Get<Rigidbody>();
		if ( bb.IsValid() )
		{
			_impactSpeed = bb.Velocity.Length;
			if ( LogPinCrash )
			{
				var mph = _impactSpeed * (_ball.RealBallInches / (_ball.Radius * 2f)) / 17.6f;
				Log.Info( $"[terryball][impact] throw-line cross: speed={_impactSpeed:F0} u/s (~{mph:F1} mph)  " +
					$"angVel={bb.AngularVelocity.Length:F1}  vel={bb.Velocity}" );
			}
		}
	}

	// DEBUG (one-shot): dump mass / inertia / gravity so we can see whether MassOverride rescaled
	// the inertia tensor and how the lane's scale compares to world gravity. Logged once.
	private void Probe()
	{
		if ( !LogPinCrash ) return;
		if ( _probed ) return;
		if ( _lane.Pins.Count == 0 ) return;
		_probed = true;

		var g = Scene.PhysicsWorld.Gravity;
		var scaleCorrectG = 386f * _lane.UnitsPerInch; // real 1g in this lane's units
		Log.Info( $"[terryball][probe] world.Gravity={g} (|g|={g.Length:F0} u/s²)  " +
			$"scale-correct≈{scaleCorrectG:F0} u/s²  ratio={scaleCorrectG / MathF.Max( 1f, g.Length ):F1}x  " +
			$"unitsPerInch={_lane.UnitsPerInch:F2}  subSteps={Scene.PhysicsWorld.SubSteps}" );
		// Landmarks so the [coast] x-trace can be read against where the pins actually sit.
		Log.Info( $"[terryball][probe] x-landmarks: throwLine={_lane.ThrowLineWorldX:F0}  " +
			$"deckCenter={_lane.PinDeckWorldCenter.x:F0}  deckEnd={_lane.PinDeckEndWorldX:F0}  " +
			$"ballRadius={_ball.Radius:F0}" );

		var bb = _ball.Components.Get<Rigidbody>();
		if ( bb.IsValid() )
		{
			var r = _ball.Radius;
			var expect = 0.4f * bb.Mass * r * r; // solid sphere ⅖mr²
			Log.Info( $"[terryball][probe] BALL mass={bb.Mass:F2}kg gravScale={bb.GravityScale:F2}  " +
				$"inertia={bb.InertiaTensor}  expect(⅖mr²)≈{expect:F0}  massCenter={bb.MassCenter}" );
		}

		var pin = _lane.Pins[0].Object;
		var pb = pin.IsValid() ? pin.Components.Get<Rigidbody>() : null;
		if ( pb.IsValid() )
			Log.Info( $"[terryball][probe] PIN1 mass={pb.Mass:F2}kg gravScale={pb.GravityScale:F2}  " +
				$"inertia={pb.InertiaTensor}  massCenter={pb.MassCenter}" );
	}

	// DEBUG: after the rack has settled (end of watch), dump each pin's final state so we can see the
	// leave pattern AND why each "down" pin is down — actually fell over (tilt) vs merely shoved off
	// its spot while still upright (disp). Distinguishes a too-efficient cascade from over-lenient
	// scoring before we change anything.
	private void LogLeave()
	{
		if ( !LogPinCrash ) return;
		var cosTilt = MathF.Cos( MathX.DegreeToRadian( TiltThresholdDegrees ) );
		var standing = new List<int>();
		foreach ( var p in _lane.Pins )
		{
			if ( !p.Object.IsValid() ) continue;
			var up = p.Object.WorldRotation.Up.Dot( Vector3.Up ).Clamp( -1f, 1f );
			var tilt = MathX.RadianToDegree( MathF.Acos( up ) );
			var disp = (p.Object.WorldPosition - p.Home).WithZ( 0f ).Length / _lane.UnitsPerInch;
			var tilted = up < cosTilt;
			var shoved = disp > DisplacementThresholdInches;
			if ( !tilted && !shoved ) standing.Add( p.Number );
			Log.Info( $"[terryball][leave] Pin{p.Number} {(tilted || shoved ? "DOWN" : "UP  ")} " +
				$"tilt={tilt:F0}° disp={disp:F1}in  reason={(tilted ? "fell" : shoved ? "shoved-but-upright" : "-")}" );
		}
		Log.Info( $"[terryball][leave] standing={string.Join( ",", standing )}  ({standing.Count} left)" );
	}

	// The watch has ended and the pins have settled (BOWLER side). Count what this ball knocked from
	// our authoritative local pins, report it to the host for the scorecard, and apply the bowling
	// rules locally to either respot for our next ball or end our turn (the host then rotates).
	private void ProcessSettledBall()
	{
		LogLeave();

		// Pins down now (Score() marks both physics-down pins AND ones swept on an earlier ball this
		// frame), minus the ones already down entering this ball, is what THIS ball knocked over.
		var knocked = Math.Clamp( DownCount - _ballStartDown, 0, 10 );
		_localBalls.Add( knocked );

		var pocket = _ball?.PocketHit ?? false; // did this ball drive the pocket (touched 1 + 2/3)?
		var turnDone = DecideLocal();
		LocalAvatar?.ReportBall( knocked, PinDownBits, pocket ); // host books it onto the card right away
		_ball?.ResetContacts(); // next ball starts with a clean contact set

		if ( turnDone )
		{
			// Solo: stay in the ball frame after frame — no settee round-trip. Just end the turn; the host
			// rotates (same player, next frame) and our next BeginTurn flies us back up out of the pit to
			// roll. Multiplayer: the next bowler is always someone else, so crane us back to our seat and
			// set us down (FinishDrop then sends EndTurn).
			if ( RosterNames.Count <= 1 )
			{
				State = RoundState.Spent;  // idle in the pit until our next turn flies us back up
				LocalAvatar?.EndTurn();
			}
			else
			{
				StartCrane( CraneJob.Dropoff );
			}
		}
		// Not done: DecideLocal already set up our next ball (ContinueFrame → Return crane), still in-ball.
	}

	// Bowling's flow control for OUR turn, on OUR local ball list. Returns true if the turn is over
	// (host rotates to the next bowler); otherwise it has already set up our next ball. Uses the
	// synced CurrentFrame so the 10th-frame rules apply even when the bowler isn't the host.
	private bool DecideLocal()
	{
		var frame = _localBalls;
		var tenth = CurrentFrame == 9;
		var ball = frame.Count; // balls thrown so far this frame (1, 2 or 3)

		if ( !tenth )
		{
			var strike = frame[0] == 10;
			if ( strike || ball >= 2 ) return true;  // strike (1 ball) or open/spare (2 balls): done
			ContinueFrame( fresh: false );           // second ball at the standing pins
			return false;
		}

		// --- 10th frame ---
		if ( ball == 1 )
		{
			ContinueFrame( fresh: frame[0] == 10 );  // strike clears the deck -> fresh rack
			return false;
		}
		if ( ball == 2 )
		{
			var earnedThird = frame[0] == 10 || frame[0] + frame[1] >= 10; // strike or spare
			if ( !earnedThird ) return true;
			var fresh = frame[0] == 10 ? frame[1] == 10 : true;
			ContinueFrame( fresh );
			return false;
		}
		return true; // third ball thrown — our tenth is done
	}

	// HOST bookkeeping. One bowler's frame is done: rotate to the next bowler at the SAME frame; once
	// the last bowler finishes, step everyone to the next frame; after the last bowler's frame 10 the
	// game's over. The host racks a clean deck (it owns the rack in the gap) before spotlighting the next
	// bowler, who then just grabs it and rolls on their own machine (BeginTurn) when ActiveBowlerId arrives.
	private void AdvanceTurn()
	{
		_current++;
		if ( _current >= _players.Count )
		{
			_current = 0;
			_frame++;
			if ( _frame >= 10 )
			{
				EndGame();
				return;
			}
		}

		// Reclaim the rack from the bowler who just finished and present a fresh, settled deck on the host
		// BEFORE the next bowler is spotlighted — this is what clears the previous bowler's downed pins,
		// authoritatively and with no bowler→bowler ownership handoff to race the respot.
		HostRackForNextBowler();
		SetActiveBowler();
		PublishScores();
	}

	// DIRECTOR: in the gap between turns the director owns the ball + rack. Take them back from the bowler
	// who just finished, drop a clean full deck and park the ball, so the next bowler grabs a stable,
	// already-correct rack (no contested writes, no leftover pins). No-op outside a live session.
	private void HostRackForNextBowler( bool instant = false )
	{
		if ( !IsDirector ) return;
		HostReclaimThrowOwnership();
		FullRack( instant );
		// No PlaceBallHome: the ball isn't reset to the start between turns. It sits where the last crane
		// left it (the pit, or the previous bowler's seat) and the next bowler's pickup flies it on.
	}

	// Stay in the same frame for another ball. fresh=true racks a clean deck (10th-frame bonus
	// after a cleared rack); fresh=false sweeps the pins this ball downed and respots the rest.
	private void ContinueFrame( bool fresh )
	{
		if ( fresh )
		{
			FullRack();
			_ballStartDown = 0;
		}
		else
		{
			SweepAndRespot();
			_ballStartDown = _sweptPins.Count; // everything still down is now swept off the deck
		}
		// We're still in the ball — the ball comes up out of the backstop and flies back to the start, then
		// rolls (no teleport-reset to the run-up).
		StartCrane( CraneJob.Return );
	}

	private void EndGame()
	{
		_gameOver = true;
		State = RoundState.Rolling;
		if ( _camera.IsValid() ) _camera.Watching = false;
		_ball.ControlsLocked = true; // park the ball; STOP returns everyone to the lobby
		HostReclaimThrowOwnership(); // own the ball + rack so we can park/hold them on the final board
		PlaceBallHome();

		// Everyone exits the ball and is back to walking (derived from the cleared ActiveBowlerId); the
		// final board stays up until STOP, which relinquishes the lane back to the host lobby.
		ActiveBowlerId = Guid.Empty;

		PublishScores();
		Log.Info( "[terryball][game] complete — final board up; press STOP for the lobby" );
	}

	// Hand control back and roll the next ball from the run-up.
	private void BeginRolling()
	{
		State = RoundState.Rolling;
		_ball.ControlsLocked = false;
		if ( _camera.IsValid() ) _camera.Watching = false;
		PlaceBallHome();
	}

	// --- Crane (claw) transport ---------------------------------------------------------
	// The active bowler flies the ball kinematically from wherever it sits to the relevant spot. The camera
	// is NOT forced onto the ball here — it follows the player (it's on the ball only while they're actually
	// scooped IN), so a pickup is watched from their Terry as the claw comes to fetch them.

	// Centre the ball would rest at over a player at footSpot (their feet): low enough that the ball
	// swallows them with their PELVIS dead-centre — the same framing as when Terry rides inside the ball
	// — rather than parking the ball's bottom at their feet (which only dipped halfway onto them). Reads
	// the live avatar's pelvis height; falls back to one ball-radius if it isn't resolvable.
	private Vector3 CraneRest( Vector3 footSpot )
	{
		var av = LocalAvatar;
		var pelvisUp = av.IsValid() ? av.PelvisHeight : (_ball.IsValid() ? _ball.Radius : 100f);
		return footSpot + Vector3.Up * pelvisUp;
	}

	// A point the claw hovers at, directly above a player, before sinking / after rising.
	private Vector3 CraneAbove( Vector3 footSpot ) => CraneRest( footSpot ) + Vector3.Up * CraneAboveHeight;

	// Travel time for a leg: distance ÷ the speed cap, but never less than the short-hop floor.
	private float TravelSeconds( Vector3 a, Vector3 b )
		=> MathF.Max( CraneMinTravelSeconds, (b - a).Length / (CraneSpeed > 1f ? CraneSpeed : 1f) );

	private void Step( CraneStepKind kind, Vector3 pos = default, float seconds = 0f, bool track = false )
		=> _craneSteps.Add( new CraneStep { Kind = kind, Pos = pos, Seconds = seconds, Track = track } );

	// The bowler's LIVE foot position, for a pickup that tracks them. Falls back to the captured grab spot
	// if the avatar's momentarily unavailable. Used only pre-scoop (after the scoop they ARE the ball).
	private Vector3 PickupFoot()
	{
		var av = LocalAvatar;
		return av.IsValid() ? av.WorldPosition : _grabPos;
	}

	// Step the ball from -> to, capped at maxDist this frame (so a moving target is chased, not snapped).
	private static Vector3 MoveToward( Vector3 from, Vector3 to, float maxDist )
	{
		var d = to - from;
		var len = d.Length;
		return (len <= maxDist || len < 0.0001f) ? to : from + d / len * maxDist;
	}

	// Lay out a trip's choreography. The ball always moves from wherever it currently sits.
	private void BuildCraneSteps( CraneJob job )
	{
		_craneSteps.Clear();
		var lift = _ball.WorldPosition + Vector3.Up * CraneLiftHeight;

		// Crane disabled: no claw animation — keep only the logical steps (body↔ball swap, roll/handoff)
		// and teleport the ball straight to where each leg would have ended.
		if ( !CraneEnabled )
		{
			switch ( job )
			{
				case CraneJob.Pickup:
					Step( CraneStepKind.Snap, CraneRest( _grabPos ) ); // over the bowler for the scoop
					Step( CraneStepKind.Scoop );
					Step( CraneStepKind.Snap, _ballHome );
					Step( CraneStepKind.Roll );
					break;
				case CraneJob.Dropoff:
					Step( CraneStepKind.Snap, CraneRest( _dropSpot ) );
					Step( CraneStepKind.Drop );
					Step( CraneStepKind.EndDrop );
					break;
				default: // Return
					Step( CraneStepKind.Snap, _ballHome );
					Step( CraneStepKind.Roll );
					break;
			}
			return;
		}

		switch ( job )
		{
			case CraneJob.Pickup:
				// Fetch the bowler, who keeps walking the WHOLE time (controls never locked). Every pre-scoop
				// leg tracks their LIVE position (CraneAbove(_grabPos) is just the seed), so the claw chases
				// them and only scoops once it's actually on them — no yank-from-afar if they wander off.
				Step( CraneStepKind.Travel, CraneAbove( _grabPos ), track: true );        // fly above them
				Step( CraneStepKind.Wait, seconds: CraneAbovePauseSeconds, track: true ); // hover, staying over them
				Step( CraneStepKind.Descend );                                            // sink onto them; scoops when on
				Step( CraneStepKind.Scoop );
				Step( CraneStepKind.Rise );                                               // lift straight up from the scoop
				Step( CraneStepKind.Wait, seconds: CraneAbovePauseSeconds );
				Step( CraneStepKind.Travel, _ballHome );
				Step( CraneStepKind.Roll );
				break;

			case CraneJob.Dropoff:
				// Post-throw in the pit — float up, hover, fly to ABOVE our seat, line up, sink us down,
				// lift clear, then hand off.
				Step( CraneStepKind.Move, lift, CraneLiftSeconds );
				Step( CraneStepKind.Wait, seconds: CraneHoverSeconds );
				Step( CraneStepKind.Travel, CraneAbove( _dropSpot ) );
				Step( CraneStepKind.Wait, seconds: CraneAbovePauseSeconds );
				Step( CraneStepKind.Move, CraneRest( _dropSpot ), CraneSinkSeconds );
				Step( CraneStepKind.Drop );
				Step( CraneStepKind.Move, CraneAbove( _dropSpot ), CraneSinkSeconds );
				Step( CraneStepKind.Wait, seconds: CraneAbovePauseSeconds );
				Step( CraneStepKind.EndDrop );
				break;

			default: // Return — already in the ball: float up out of the pit, hover, fly to the start, roll.
				Step( CraneStepKind.Move, lift, CraneLiftSeconds );
				Step( CraneStepKind.Wait, seconds: CraneHoverSeconds );
				Step( CraneStepKind.Travel, _ballHome );
				Step( CraneStepKind.Roll );
				break;
		}
	}

	private void StartCrane( CraneJob job )
	{
		_craneJob = job;
		State = RoundState.Craning;
		_ball.ControlsLocked = true;
		_ball.SetCraneMode( true );
		_ballCraned = true;

		// Return/Dropoff both start by lifting the ball straight up out of the pit (step 0), so hold the
		// pin-deck watch view through that rise and release it at the apex (UpdateCrane). A fresh Pickup has
		// the camera on the walking Terry anyway, and a crane-disabled reset just snaps — both release now.
		_releaseWatchAtApex = CraneEnabled && (job == CraneJob.Return || job == CraneJob.Dropoff);
		if ( _camera.IsValid() && !_releaseWatchAtApex ) _camera.Watching = false;

		BuildCraneSteps( job );
		BeginStep( 0 );
	}

	// Enter step i. Timed steps (Move/Travel/Wait) set up their duration; instant steps act and chain on.
	private void BeginStep( int i )
	{
		_craneStep = i;
		if ( i < 0 || i >= _craneSteps.Count ) return;

		var s = _craneSteps[i];
		_craneStepElapsed = 0f;
		_craneStepFrom = _ball.WorldPosition;
		_craneStepTo = s.Pos;

		switch ( s.Kind )
		{
			case CraneStepKind.Move: _craneStepDuration = MathF.Max( 0.05f, s.Seconds ); break;
			case CraneStepKind.Rise:
				_craneStepTo = _craneStepFrom + Vector3.Up * CraneAboveHeight; // straight up from here
				_craneStepDuration = MathF.Max( 0.05f, CraneSinkSeconds );
				break;
			// Tracked travel chases a live target (no fixed end time); plain travel is distance/speed.
			case CraneStepKind.Travel: _craneStepDuration = s.Track ? 0f : TravelSeconds( _craneStepFrom, s.Pos ); break;
			case CraneStepKind.Wait: _craneStepDuration = MathF.Max( 0f, s.Seconds ); break;
			// Ends on proximity (the ball reaching the player); this is just a don't-hang-forever safety cap.
			case CraneStepKind.Descend: _craneStepDuration = CraneSinkSeconds + 3f; break;

			case CraneStepKind.Snap: _ball.CraneMoveTo( s.Pos ); BeginStep( i + 1 ); break; // instant teleport
			case CraneStepKind.Scoop: LocalAvatar?.ScoopIntoBall(); BeginStep( i + 1 ); break; // body → ball
			case CraneStepKind.Drop: LocalAvatar?.DropAtReturnSpot(); BeginStep( i + 1 ); break; // ball → body
			case CraneStepKind.Roll: FinishRoll(); break;
			case CraneStepKind.EndDrop: FinishDrop(); break;
		}
	}

	private void UpdateCrane()
	{
		if ( _craneStep < 0 || _craneStep >= _craneSteps.Count ) return;
		var s = _craneSteps[_craneStep];

		_craneStepElapsed += Time.Delta;
		var t = (_craneStepDuration > 0f ? _craneStepElapsed / _craneStepDuration : 1f).Clamp( 0f, 1f );

		// How fast the claw chases a moving target this frame (also the descend rate).
		var chase = CraneSpeed * Time.Delta;

		switch ( s.Kind )
		{
			case CraneStepKind.Move:
			case CraneStepKind.Rise:
				_ball.CraneMoveTo( Vector3.Lerp( _craneStepFrom, _craneStepTo, t ) );
				break;
			case CraneStepKind.Travel:
				if ( s.Track )
				{
					// Chase a point directly above the LIVE bowler; finish once we're over them.
					var above = CraneAbove( PickupFoot() );
					var next = MoveToward( _ball.WorldPosition, above, chase );
					_ball.CraneMoveTo( next );
					if ( (next - above).WithZ( 0f ).Length <= _ball.Radius ) BeginStep( _craneStep + 1 );
					return;
				}
				// Linear (so the speed cap holds) with a vertical bulge to clear the rack.
				_ball.CraneMoveTo( Vector3.Lerp( _craneStepFrom, s.Pos, t )
					+ Vector3.Up * CraneArcHeight * MathF.Sin( t * MathF.PI ) );
				break;
			case CraneStepKind.Wait:
				// Tracked hover: stay locked above the live bowler. Plain hover: hold position.
				_ball.CraneMoveTo( s.Track ? MoveToward( _ball.WorldPosition, CraneAbove( PickupFoot() ), chase ) : _craneStepFrom );
				break;
			case CraneStepKind.Descend:
			{
				// Sink onto the live bowler, chasing them horizontally so they can't outrun the claw. The
				// scoop fires only once the ball is actually ON them (close + at rest height) — not on a timer
				// from a stale spot. The duration is just a safety cap so a wedged case can't hang the turn.
				var rest = CraneRest( PickupFoot() );
				var sinkRate = CraneAboveHeight / MathF.Max( 0.05f, CraneSinkSeconds ) * Time.Delta;
				var pos = _ball.WorldPosition;
				var horiz = MoveToward( pos.WithZ( 0f ), rest.WithZ( 0f ), chase );
				var z = pos.z + Math.Clamp( rest.z - pos.z, -sinkRate, sinkRate );
				var next = horiz.WithZ( z );
				_ball.CraneMoveTo( next );

				var onThem = (next - rest).WithZ( 0f ).Length <= _ball.Radius * 0.6f
				             && MathF.Abs( next.z - rest.z ) <= _ball.Radius * 0.5f;
				if ( onThem || _craneStepElapsed >= _craneStepDuration ) BeginStep( _craneStep + 1 );
				return;
			}
		}

		if ( t >= 1f )
		{
			// The ball just topped out at its lift apex (step 0, the rise out of the pit) — release the camera
			// off the pin-deck watch view so it eases onto the ball and follows it back to the run-up.
			if ( _releaseWatchAtApex && _craneStep == 0 )
			{
				_releaseWatchAtApex = false;
				if ( _camera.IsValid() ) _camera.Watching = false;
			}
			BeginStep( _craneStep + 1 );
		}
	}

	// A flight that ends at the lane start: restore physics and roll.
	private void FinishRoll()
	{
		_craneJob = CraneJob.None;
		_ball.SetCraneMode( false );
		_ballCraned = false;
		BeginRolling();
	}

	// A dropoff flight that ends at the bowler's seat: leave the ball frozen-kinematic right there (the next
	// bowler's pickup flies it on — no reset to the start) and tell the host our turn's over.
	private void FinishDrop()
	{
		_craneJob = CraneJob.None;     // _ballCraned stays true: the ball is parked frozen at the seat
		_ball.ControlsLocked = true;
		State = RoundState.Spent;
		LocalAvatar?.EndTurn();
	}

	// STOP / disconnect / game-end interrupted a crane (or left the ball parked frozen after a dropoff):
	// restore the ball's physics so it isn't stuck collider-off (the host then reclaims + parks it).
	private void AbortCrane()
	{
		_craneJob = CraneJob.None;
		_craneSteps.Clear();
		_craneStep = 0;
		_releaseWatchAtApex = false;
		if ( _ballCraned && _ball.IsValid() ) _ball.SetCraneMode( false );
		_ballCraned = false;
		if ( State == RoundState.Craning ) State = RoundState.Rolling;
	}

	// --- Approach-B throw: the active bowler runs the whole turn on its own physics ----------------

	// We just became the active bowler: grab the shared ball + rack and roll. The host already racked a
	// clean, settled deck while it held ownership in the gap (HostRackForNextBowler), so we just take it
	// over and start — no respot here, which is what used to race the previous bowler's handoff.
	private void BeginTurn()
	{
		_localBalls.Clear();
		_sweptPins.Clear();
		_ballStartDown = 0;
		PinDownBits = 0;
		TakeThrowOwnership(); // grab the shared ball + rack so OUR physics drives this turn

		var av = LocalAvatar;
		if ( av.IsValid() && av.Carried )
		{
			// Already in the ball (solo, frame after frame): no fetch — fly the ball up out of the pit to
			// the start and roll. Keep the grab/drop spot from when we were first picked up.
			StartCrane( CraneJob.Return );
			return;
		}

		// Fresh pickup: note where the claw is grabbing us from. We get set back down on this exact spot at
		// turn's end — unless we were grabbed off the settee (e.g. stood up at the lane station after
		// pressing START), in which case we're dropped at the settee middle instead.
		_grabPos = av.IsValid() ? av.WorldPosition : (_lane.IsValid() ? _lane.SetteeFloorCenter : _ballHome);
		_grabOnSettee = _lane.IsValid() && _lane.IsOnSettee( _grabPos );
		_dropSpot = _grabOnSettee ? _grabPos : (_lane.IsValid() ? _lane.SetteeFloorCenter : _grabPos);
		av?.SetReturnSpot( _dropSpot );

		// Fly out from wherever the ball sits, fetch us, then on to the lane start to roll.
		StartCrane( CraneJob.Pickup );
	}

	// Assume ownership of the lane's networked ball + ten pins (they're tagged Takeover), so this
	// machine simulates the throw and the engine replicates it to every spectator. No-op outside a
	// live session (editor/solo before a lobby) — the objects are plain local ones there.
	private void TakeThrowOwnership()
	{
		if ( !Networking.IsActive ) return;
		if ( _ball.IsValid() && _ball.GameObject.Network.Active && !_ball.GameObject.Network.IsOwner )
			_ball.GameObject.Network.TakeOwnership();
		foreach ( var p in _lane.Pins )
			if ( p.Object.IsValid() && p.Object.Network.Active && !p.Object.Network.IsOwner )
				p.Object.Network.TakeOwnership();
	}

	// DIRECTOR: pull the ball + rack back to the director between/after turns (lobby, game over), so it can
	// park the ball and re-rack on objects it owns rather than fighting the last bowler's ownership. The
	// ball + pins are Takeover, so a non-host director can grab them too.
	private void HostReclaimThrowOwnership()
	{
		if ( !IsDirector ) return;
		if ( _ball.IsValid() && _ball.GameObject.Network.Active && !_ball.GameObject.Network.IsOwner )
			_ball.GameObject.Network.TakeOwnership();
		foreach ( var p in _lane.Pins )
			if ( p.Object.IsValid() && p.Object.Network.Active && !p.Object.Network.IsOwner )
				p.Object.Network.TakeOwnership();
	}

	/// <summary>Derive the down-mask from the lane's (replicated) pins, the same test <see cref="Score"/>
	/// uses — for the spectator panels, which don't run the authoritative scoring pass. A pin reads down
	/// if it's swept off the deck (disabled) or tipped/shoved past the thresholds.</summary>
	private int EvaluateDownBits()
	{
		if ( !_lane.IsValid() ) return PinDownBits;
		var cosTilt = MathF.Cos( MathX.DegreeToRadian( TiltThresholdDegrees ) );
		var maxOffset = DisplacementThresholdInches * _lane.UnitsPerInch;
		var maxOffsetSq = maxOffset * maxOffset;

		var bits = 0;
		foreach ( var p in _lane.Pins )
		{
			bool down;
			if ( !p.Object.IsValid() || !p.Object.Enabled )
			{
				down = true; // swept off the deck (disabled by the bowler, replicated to us)
			}
			else if ( p.Pin.IsValid() && p.Pin.Phase == Pin.PinPhase.Racking )
			{
				down = false; // being re-racked (fading in / gliding home) — counts as standing
			}
			else if ( p.Pin.IsValid() && p.Pin.Phase == Pin.PinPhase.Down )
			{
				down = true; // fading out where it fell, about to be swept off the deck
			}
			else
			{
				var tilted = p.Object.WorldRotation.Up.Dot( Vector3.Up ) < cosTilt;
				var offsetSq = (p.Object.WorldPosition - p.Home).WithZ( 0f ).LengthSquared;
				down = tilted || offsetSq > maxOffsetSq;
			}
			if ( down ) bits |= 1 << p.Number;
		}
		return bits;
	}

	/// <summary>Director side (routed here from the bowler's TerryAvatar.ReportBall): book a settled ball's
	/// pinfall onto the active player's card, reflect the leave on our own panels, and classify the call.
	/// [Rpc.Owner] — invoked by the bowler client, runs on whichever player owns this lane's director.</summary>
	[Rpc.Owner]
	public void BookBall( int knocked, int downMask, bool pocket )
	{
		if ( !IsDirector || Phase != GamePhase.Playing ) return;

		PinDownBits = downMask;
		if ( _current >= 0 && _current < _players.Count && _frame >= 0 && _frame < 10 )
		{
			var player = _players[_current];
			player.Frames[_frame].Add( knocked );

			// Classify what just happened into an announcer call and broadcast it to every client
			// (they play it 2D, gated to their own lane, and pop the call-out banner). A split also
			// gets recorded so the scoreboard can circle the count.
			var call = ClassifyCall( player, _frame, downMask, pocket, out var isSplit );
			if ( isSplit ) player.Splits.Add( (_frame, player.Frames[_frame].Count - 1) );
			if ( !string.IsNullOrEmpty( call ) ) Announce( call );
		}
		PublishScores();
	}

	/// <summary>Director side (routed from the bowler's TerryAvatar.EndTurn once their dropoff crane has set
	/// them back on the settee): rotate to the next bowler — which reclaims + re-racks the ball for the
	/// pickup. [Rpc.Owner], runs on this lane's director owner.</summary>
	[Rpc.Owner]
	public void EndTurnRpc()
	{
		if ( !IsDirector || Phase != GamePhase.Playing ) return;
		if ( Rpc.Caller is null || Rpc.Caller.Id != ActiveBowlerId ) return; // only the player who's up can end it
		AdvanceTurn();
	}

	/// <summary>Director side (routed from the bowler's TerryAvatar.RequestLeaveBall): the active bowler hit
	/// the escape hatch — end their turn so the game moves on and they drop out of the ball. Recovers from a
	/// wedged turn without anyone having to restart the game. [Rpc.Owner].</summary>
	[Rpc.Owner]
	public void LeaveBallRpc()
	{
		if ( !IsDirector || Phase != GamePhase.Playing ) return;
		if ( Rpc.Caller is null || Rpc.Caller.Id != ActiveBowlerId ) return; // only the player who's up can bail
		Log.Info( "[terryball][game] bowler bailed out of the ball — advancing the turn" );
		AdvanceTurn();
	}

	// Drop every pin back onto its spot, upright and dead still — a clean rack. Animated by default (pins
	// fade/glide home); instant snaps them for lobby transitions where there's no throw to watch it play.
	private void FullRack( bool instant = false )
	{
		_sweptPins.Clear();
		foreach ( var p in _lane.Pins )
			RespotPin( p, instant );
	}

	// Sweep the pins this ball knocked down (fade them out then off the deck so they can't foul the next
	// ball) and glide the ones still standing back onto their exact spots — the between-ball respot.
	private void SweepAndRespot()
	{
		foreach ( var s in _statuses )
		{
			var p = _lane.Pins.FirstOrDefault( pp => pp.Number == s.Number );
			if ( !p.Pin.IsValid() ) continue;
			if ( s.Down )
			{
				_sweptPins.Add( p.Number );
				p.Pin.Sweep(); // fade out where it fell, then off the deck
			}
			else
			{
				RespotPin( p, instant: false ); // nudged-but-standing pin glides back onto its home spot
			}
		}
	}

	/// <summary>Pin impact audio is muted until this time — set whenever pins are teleported onto their
	/// spots (rack/respot), so the settling contacts from re-racking don't clack while the ball resets.
	/// Read by <see cref="PinImpactAudio"/> (which also skips the network broadcast while muted).</summary>
	public float PinAudioQuietUntil { get; private set; }

	/// <summary>How long (seconds) pin audio stays muted after a rack/respot — long enough for the teleported
	/// pins to settle onto the deck without clacking.</summary>
	[Property, Group( "Settle" )] public float PinRackQuietSeconds { get; set; } = 0.5f;

	/// <summary>Seconds of delay added per pin when re-racking, so the deck fills in a quick cascade instead
	/// of every pin landing at once. Pins land in descending number order (10, 9, 8 … 1), so the head pin is
	/// last. Kept small — just enough to read as a wave.</summary>
	[Property, Group( "Settle" )] public float RackStaggerSeconds { get; set; } = 0.05f;

	// Stagger index for a pin number: pin 10 lands first (0), the head pin (1) last (9). Multiply by
	// RackStaggerSeconds for the per-pin start delay.
	private static int RackOrder( int number ) => 10 - number;

	// Respot one pin onto its home spot. Animated (default): the pin fades in (if it was swept) and glides
	// kinematically back onto its spot from wherever it lies — see Pin.RackTo. instant snaps it there with
	// no animation (lobby / first placement). The pin owns the motion; we just kick it off.
	private void RespotPin( LaneBuilder.PinPlacement p, bool instant )
	{
		if ( !p.Pin.IsValid() ) return;
		var delay = instant ? 0f : RackOrder( p.Number ) * RackStaggerSeconds;
		p.Pin.RackTo( p.Home, instant, delay );
		// Mute the settling clacks from the glide onto the spot — extend past this pin's stagger so the
		// last row to land is still covered.
		PinAudioQuietUntil = MathF.Max( PinAudioQuietUntil, Time.Now + delay + PinRackQuietSeconds );
	}

	// Drop the ball on its run-up spot, upright and dead still.
	private void PlaceBallHome()
	{
		if ( !_ball.IsValid() ) return;
		_ball.WorldPosition = _ballHome;
		_ball.WorldRotation = _ballHomeRot;
		var ballBody = _ball.Components.Get<Rigidbody>();
		if ( ballBody.IsValid() )
		{
			ballBody.Velocity = Vector3.Zero;
			ballBody.AngularVelocity = Vector3.Zero;
		}
	}

	// Re-evaluate every pin against the alley's "still on its spot, still upright?" test.
	private void Score()
	{
		var cosTilt = MathF.Cos( MathX.DegreeToRadian( TiltThresholdDegrees ) );
		var maxOffset = DisplacementThresholdInches * _lane.UnitsPerInch;
		var maxOffsetSq = maxOffset * maxOffset;

		_statuses.Clear();
		var bits = 0;
		foreach ( var p in _lane.Pins )
		{
			var down = true;
			// A pin swept off the deck earlier this frame stays down for the rest of the frame,
			// regardless of where its (disabled) body is sitting.
			if ( _sweptPins.Contains( p.Number ) )
			{
				down = true;
			}
			// Mid re-rack (fading in / gliding onto its spot): it's on its way to standing, so read it as
			// UP — otherwise the readout flickers "down" while a reset pin is still tilted mid-glide.
			else if ( p.Pin.IsValid() && p.Pin.Phase == Pin.PinPhase.Racking )
			{
				down = false;
			}
			else if ( p.Object.IsValid() )
			{
				var tilted = p.Object.WorldRotation.Up.Dot( Vector3.Up ) < cosTilt;
				var offsetSq = (p.Object.WorldPosition - p.Home).WithZ( 0f ).LengthSquared;
				down = tilted || offsetSq > maxOffsetSq;
			}
			_statuses.Add( new PinStatus( p.Number, down ) );
			if ( down ) bits |= 1 << p.Number;
		}
		PinDownBits = bits; // the bowler's authoritative leave, for every pin panel + the stream
	}

	// --- Score view for the overlay -----------------------------------------------------
	// Read-only snapshots the razor scoreboard renders; computed here so the panel stays a dumb
	// view. One FrameView per frame (its ball marks + running total once scorable), one
	// PlayerView per player.

	/// <summary>One frame's display: the per-ball mark strings (X, /, -, digits) and the running
	/// cumulative total, which is null until the frame can actually be scored (its bonus is in).</summary>
	public sealed class FrameView
	{
		public List<string> Balls { get; init; } = new();
		public int? Score { get; init; }
		public bool Active { get; init; }
		/// <summary>Indices into <see cref="Balls"/> whose count box is a split (the scoreboard circles them).</summary>
		public HashSet<int> SplitBalls { get; init; } = new();
	}

	/// <summary>One player's row: name, ten frames, grand total and whether it's their turn.</summary>
	public sealed class PlayerView
	{
		public string Name { get; init; } = "";
		public List<FrameView> Frames { get; init; } = new();
		public int Total { get; init; }
		public bool Active { get; init; }
	}

	/// <summary>Build the scoreboard model for every player from the SYNCED state, so the host and
	/// every client render an identical board (the host publishes its authoritative scoring into
	/// RosterNames/RosterScores; this just reads it back). Cheap; called on each panel rebuild.</summary>
	public List<PlayerView> BuildScoreView()
	{
		var views = new List<PlayerView>();

		// Lobby: there's no locked roster yet (RosterNames is published on START), so show who has
		// JOINED right now — read straight off the synced per-avatar Joined/DisplayName so the host and
		// every client list the same bowlers, ordered by owner id for a stable order. Empty cards; the
		// real scorecard takes over once the host presses START.
		if ( Phase != GamePhase.Playing )
		{
			var joined = Scene.GetAllComponents<TerryAvatar>()
				.Where( a => a.IsValid() && a.Joined )
				.OrderBy( a => a.GameObject.Network.Owner?.Id.ToString() ?? "" );
			foreach ( var a in joined )
			{
				var frames = new List<FrameView>( 10 );
				for ( var fr = 0; fr < 10; fr++ )
					frames.Add( new FrameView { Balls = BallMarks( new List<int>(), fr == 9 ) } );
				views.Add( new PlayerView { Name = a.DisplayName, Frames = frames } );
			}
			return views;
		}

		var over = GameComplete;
		for ( var pi = 0; pi < RosterNames.Count; pi++ )
		{
			var name = RosterNames[pi];
			var framesData = pi < RosterScores.Count ? DecodeFrames( RosterScores[pi] ) : DecodeFrames( "" );
			var splits = pi < RosterSplits.Count ? DecodeSplits( RosterSplits[pi] ) : new();
			var player = new PlayerScore( name );
			for ( var f = 0; f < 10; f++ ) player.Frames[f] = framesData[f];

			var cum = ScoreThrough( player, out var have );
			var frames = new List<FrameView>( 10 );
			var total = 0;
			for ( var fr = 0; fr < 10; fr++ )
			{
				if ( have[fr] ) total = cum[fr];
				frames.Add( new FrameView
				{
					Balls = BallMarks( player.Frames[fr], fr == 9 ),
					Score = have[fr] ? cum[fr] : null,
					Active = pi == CurrentPlayer && fr == CurrentFrame && !over,
					SplitBalls = splits.TryGetValue( fr, out var sb ) ? sb : new(),
				} );
			}
			views.Add( new PlayerView
			{
				Name = name,
				Frames = frames,
				Total = total,
				Active = pi == CurrentPlayer && !over,
			} );
		}
		return views;
	}

	/// <summary>Standard 10-pin cumulative scoring. Returns the running total per frame; the
	/// matching <paramref name="have"/> flag is false for any frame not yet fully scorable (e.g.
	/// a strike whose two bonus balls haven't been thrown).</summary>
	private static int[] ScoreThrough( PlayerScore player, out bool[] have )
	{
		// Flatten to a roll sequence (a frames-1–9 strike is the single value 10), which the
		// classic strike/spare look-ahead walks cleanly — including the 10th frame's bonus balls.
		var rolls = new List<int>();
		foreach ( var f in player.Frames )
			foreach ( var b in f )
				rolls.Add( b );

		var cum = new int[10];
		have = new bool[10];
		var running = 0;
		var i = 0;
		for ( var fr = 0; fr < 10 && i < rolls.Count; fr++ )
		{
			if ( fr == 9 )
			{
				// Last frame is just the sum of its balls, but only "have" it once it's complete:
				// 3 balls if the first ball is a strike or the first two make a spare, else 2.
				var need = rolls[i] == 10 || (i + 1 < rolls.Count && rolls[i] + rolls[i + 1] == 10) ? 3 : 2;
				if ( rolls.Count - i >= need )
				{
					for ( var k = i; k < rolls.Count; k++ ) running += rolls[k];
					cum[fr] = running;
					have[fr] = true;
				}
				break;
			}

			if ( rolls[i] == 10 ) // strike: 10 + next two balls
			{
				if ( i + 2 < rolls.Count ) { running += 10 + rolls[i + 1] + rolls[i + 2]; cum[fr] = running; have[fr] = true; i += 1; }
				else break;
			}
			else if ( i + 1 < rolls.Count && rolls[i] + rolls[i + 1] == 10 ) // spare: 10 + next ball
			{
				if ( i + 2 < rolls.Count ) { running += 10 + rolls[i + 2]; cum[fr] = running; have[fr] = true; i += 2; }
				else break;
			}
			else if ( i + 1 < rolls.Count ) // open frame, both balls thrown
			{
				running += rolls[i] + rolls[i + 1]; cum[fr] = running; have[fr] = true; i += 2;
			}
			else break; // only the first ball of an open frame so far
		}
		return cum;
	}

	private static string Sym( int n ) => n == 0 ? "-" : n.ToString();

	// The 1–3 mark strings shown in a frame's little boxes: X for a strike, / for a spare, - for
	// a miss, digits otherwise. The 10th frame has three boxes and its own strike/spare rules
	// per ball (a cleared deck re-racks, so a later ball can be a strike or spare in its own right).
	private static List<string> BallMarks( List<int> f, bool tenth )
	{
		if ( !tenth )
		{
			var b = new List<string> { "", "" };
			if ( f.Count >= 1 )
			{
				if ( f[0] == 10 ) { b[1] = "X"; }          // strike sits in the right box
				else
				{
					b[0] = Sym( f[0] );
					if ( f.Count >= 2 ) b[1] = f[0] + f[1] == 10 ? "/" : Sym( f[1] );
				}
			}
			return b;
		}

		var t = new List<string> { "", "", "" };
		if ( f.Count >= 1 ) t[0] = f[0] == 10 ? "X" : Sym( f[0] );
		if ( f.Count >= 2 )
			t[1] = f[0] == 10 ? (f[1] == 10 ? "X" : Sym( f[1] ))   // fresh rack after the strike
				: (f[0] + f[1] == 10 ? "/" : Sym( f[1] ));         // spare/open on the first rack
		if ( f.Count >= 3 )
		{
			// The third ball is on a fresh rack after a double-strike or a spare; otherwise it
			// continues the second ball's rack and can itself be a spare.
			var fresh = f[0] == 10 ? f[1] == 10 : true;
			t[2] = fresh ? (f[2] == 10 ? "X" : Sym( f[2] ))
				: (f[1] + f[2] == 10 ? "/" : Sym( f[2] ));
		}
		return t;
	}

	// --- Announcer call-outs -------------------------------------------------------------
	// The director classifies each settled ball (BookBall) into a single call id and broadcasts it.
	// Every client plays the matching VO clip (2D, gated to its own lane) and pops the lane banner.

	private readonly Random _callRng = new();

	/// <summary>Broadcast: an announcer call fired for this lane. On every client, play the clip (only
	/// if this is the LOCAL player's lane, so it can't carry across lanes) and pop the call-out banner
	/// (always — the banner is physically on this lane). Plain Broadcast now — the director owner (which
	/// may not be the host) is the caller.</summary>
	[Rpc.Broadcast]
	public void Announce( string callId )
	{
		var myLane = Lane.Of( this );
		if ( myLane.IsValid() && Lane.ForLocalPlayer( Scene ) == myLane )
			myLane.Announcer?.Play( callId );

		CalloutText = CallText( callId );
		CalloutAt = Time.Now;
	}

	/// <summary>Broadcast: play one of this lane's pin/ball impact sounds on EVERY client at
	/// <paramref name="worldPos"/>. Pin collisions only fire on the machine simulating the throw (the
	/// bowler owns the rack), so without this only the bowler would hear the crash. The sound is positional
	/// with a large falloff, so spectators and even adjacent lanes hear it, quieter with distance. The
	/// bowler (pins owner) is the only one whose listeners fire, so exactly one client broadcasts — no
	/// double-play. Routed through the director because <see cref="PinImpactAudio"/> lives on the (non-
	/// networked) lane geometry and can't host an RPC itself.</summary>
	[Rpc.Broadcast( NetFlags.Unreliable )]
	public void NetPinImpact( Vector3 worldPos, int kind, float amplitude )
	{
		var bank = Lane.Of( this )?.Builder?.PinAudio;
		bank?.PlayRaw( worldPos, (PinImpactAudio.ContactKind)kind, amplitude );
	}

	// Pin adjacency (immediate neighbours) for split detection: a split is the head pin down plus the
	// standing pins forming two or more disconnected clusters under this graph.
	private static readonly Dictionary<int, int[]> SplitAdjacency = new()
	{
		[1] = new[] { 2, 3 },
		[2] = new[] { 1, 3, 4, 5 },
		[3] = new[] { 1, 2, 5, 6 },
		[4] = new[] { 2, 5, 7, 8 },
		[5] = new[] { 2, 3, 4, 6, 8, 9 },
		[6] = new[] { 3, 5, 9, 10 },
		[7] = new[] { 4, 8 },
		[8] = new[] { 4, 5, 7, 9 },
		[9] = new[] { 5, 6, 8, 10 },
		[10] = new[] { 6, 9 },
	};

	private static readonly string[] NumberWords =
		{ "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };

	/// <summary>HOST: classify the just-booked ball on <paramref name="player"/>'s <paramref name="frameIdx"/>
	/// into one announcer call id, using the per-client <paramref name="downMask"/> for the leave pattern.
	/// Sets <paramref name="isSplit"/> when the leave is a split (so it gets circled on the scoreboard).</summary>
	private string ClassifyCall( PlayerScore player, int frameIdx, int downMask, bool pocket, out bool isSplit )
	{
		isSplit = false;
		var frame = player.Frames[frameIdx];
		var tenth = frameIdx == 9;
		var ball = frame.Count;                 // 1-based: the ball just thrown (= count after the Add)
		if ( ball < 1 ) return null;
		var knocked = frame[ball - 1];

		var (fresh, standingBefore) = RackState( frame, tenth, ball );

		if ( fresh )
		{
			// A fresh full rack: strike / streak, gutter, or a first-ball leave.
			if ( knocked == 10 )
			{
				var streak = StrikeStreak( player, frameIdx );
				if ( streak >= 12 ) return "perfect-game";
				// A flush pocket strike sometimes gets the "in the pocket" call instead of plain "strike"
				// (only a single strike — a streak's double/turkey/… always wins).
				if ( streak < 2 && pocket && _callRng.Next( 3 ) == 0 ) return "pocket";
				return streak switch
				{
					2 => "double",
					3 => "turkey",
					4 => "four-bagger",
					5 => "five-bagger",
					>= 6 => "six-pack",
					_ => "strike",
				};
			}
			if ( knocked == 0 ) return "gutter";

			// First-ball leave (1–9 down). Standing pins from the leave bits.
			var standing = new List<int>();
			for ( var n = 1; n <= 10; n++ ) if ( (downMask & (1 << n)) == 0 ) standing.Add( n );
			var headDown = (downMask & (1 << 1)) != 0;

			if ( headDown && IsSplit( standing ) ) { isSplit = true; return "split"; }
			if ( knocked == 9 )
				return standing.Count == 1 && (standing[0] == 7 || standing[0] == 10)
					? NumberWords[9]            // a ringing 7 or 10 isn't "so close"
					: "so-close";
			// First-ball count, never "nice" — that call is reserved for hard SPARE conversions below.
			return NumberWords[knocked];
		}

		// A spare attempt at a leave.
		if ( knocked == standingBefore )
		{
			// Cleared it. "Nice" is only for a genuinely hard pickup: a split conversion (always), or a
			// 3+ pin spare (occasionally). A routine 1–2 pin spare just gets the plain "spare" call.
			var clearedSplit = player.Splits.Contains( (frameIdx, ball - 2) ); // split was logged on the prior ball
			if ( clearedSplit ) return "nice";
			if ( standingBefore >= 3 && _callRng.Next( 4 ) == 0 ) return "nice";
			return "spare";
		}
		if ( knocked == 0 ) return "miss";                                                  // whiffed
		return _callRng.Next( 2 ) == 0 ? "good-try" : "open-frame";                         // left it open
	}

	/// <summary>Was the ball <paramref name="ball"/> (1-based) thrown at a fresh full rack, and how many
	/// pins were standing entering it? Mirrors the 10th-frame re-rack rules used by BallMarks/DecideLocal.</summary>
	private static (bool fresh, int standingBefore) RackState( List<int> frame, bool tenth, int ball )
	{
		if ( !tenth )
			return ball == 1 ? (true, 10) : (false, 10 - frame[0]);

		if ( ball == 1 ) return (true, 10);
		if ( ball == 2 ) return frame[0] == 10 ? (true, 10) : (false, 10 - frame[0]);
		// ball 3
		if ( frame[0] == 10 && frame[1] == 10 ) return (true, 10);   // XX_ → fresh
		if ( frame[0] == 10 ) return (false, 10 - frame[1]);         // X,n,_ → leave from ball 2
		return (true, 10);                                          // spare then bonus → fresh
	}

	/// <summary>Consecutive fresh-rack strikes ending at the player's most recent ball (1 = just this one,
	/// 2 = double, 3 = turkey, … 12 = perfect game). Counts strike BALLS, so the 10th frame's bonus
	/// strikes extend the run.</summary>
	private static int StrikeStreak( PlayerScore player, int curFrame )
	{
		var flags = new List<bool>();
		for ( var f = 0; f <= curFrame; f++ )
		{
			var frame = player.Frames[f];
			var tenth = f == 9;
			for ( var b = 1; b <= frame.Count; b++ )
			{
				var (fresh, _) = RackState( frame, tenth, b );
				flags.Add( fresh && frame[b - 1] == 10 );
			}
		}
		var run = 0;
		for ( var i = flags.Count - 1; i >= 0; i-- ) { if ( flags[i] ) run++; else break; }
		return run;
	}

	/// <summary>Is the standing-pin leave a split? True when the standing pins (head pin already known
	/// down by the caller) form two or more disconnected clusters under <see cref="SplitAdjacency"/>.</summary>
	private static bool IsSplit( List<int> standing )
	{
		if ( standing.Count < 2 ) return false;
		var standSet = new HashSet<int>( standing );
		var seen = new HashSet<int> { standing[0] };
		var stack = new Stack<int>();
		stack.Push( standing[0] );
		while ( stack.Count > 0 )
		{
			foreach ( var nb in SplitAdjacency[stack.Pop()] )
				if ( standSet.Contains( nb ) && seen.Add( nb ) ) stack.Push( nb );
		}
		return seen.Count < standing.Count; // a standing pin we couldn't reach → disconnected → split
	}

	/// <summary>The on-lane banner text for a call id (numbers show as the digit; the rest as a shout).</summary>
	private static string CallText( string callId ) => callId switch
	{
		"strike" => "STRIKE!",
		"spare" => "SPARE",
		"gutter" => "GUTTER",
		"miss" => "MISS",
		"open-frame" => "OPEN",
		"split" => "SPLIT!",
		"pocket" => "POCKET",
		"double" => "DOUBLE!",
		"turkey" => "TURKEY!",
		"four-bagger" => "FOUR-BAGGER!",
		"five-bagger" => "FIVE-BAGGER!",
		"six-pack" => "SIX-PACK!",
		"perfect-game" => "PERFECT GAME!",
		"nice" => "NICE!",
		"so-close" => "SO CLOSE!",
		"good-try" => "GOOD TRY",
		"one" => "1",
		"two" => "2",
		"three" => "3",
		"four" => "4",
		"five" => "5",
		"six" => "6",
		"seven" => "7",
		"eight" => "8",
		"nine" => "9",
		_ => callId?.ToUpperInvariant() ?? "",
	};
}
