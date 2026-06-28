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
public sealed class GameDirector : Component, Component.INetworkListener
{
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

	public enum RoundState { Rolling, Watching }
	public RoundState State { get; private set; } = RoundState.Rolling;

	// --- Multiplayer ownership ----------------------------------------------------------
	// The director is host-authoritative. Exactly one connected player is "up" at a time; the host
	// publishes their connection id here and every client compares it against its own to decide
	// whether IT drives the ball + ball camera (vs. walking its Terry around the settee). Empty =
	// nobody up (lobby / between turns), so the ball sits parked and everyone just walks.
	[Sync( SyncFlags.FromHost )] public Guid ActiveBowlerId { get; set; }

	/// <summary>Bumped by the host every time a turn is (re)assigned — including when the SAME bowler is
	/// up again (solo play, or two turns in a row). The bowler client begins a fresh turn whenever this
	/// changes, so back-to-back turns by one player still re-rack and re-roll instead of hanging on the
	/// parked ball. (Watching ActiveBowlerId alone can't catch a same-bowler hand-back — it never edges.)</summary>
	[Sync( SyncFlags.FromHost )] public int TurnSerial { get; set; }

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
	[Sync( SyncFlags.FromHost )] public GamePhase Phase { get; set; } = GamePhase.Lobby;
	/// <summary>The bowlers in this game, in turn order (only meaningful in Playing).</summary>
	[Sync( SyncFlags.FromHost )] public NetList<string> RosterNames { get; set; } = new();
	/// <summary>Per-roster-player encoded frame data, parallel to <see cref="RosterNames"/>.</summary>
	[Sync( SyncFlags.FromHost )] public NetList<string> RosterScores { get; set; } = new();
	/// <summary>Whose turn it is (index into the roster).</summary>
	[Sync( SyncFlags.FromHost )] public int CurrentPlayer { get; set; }
	/// <summary>The frame everyone's on (0–9).</summary>
	[Sync( SyncFlags.FromHost )] public int CurrentFrame { get; set; }
	/// <summary>True once the final frame of the last bowler is complete.</summary>
	[Sync( SyncFlags.FromHost )] public bool GameComplete { get; set; }

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

	private LaneBuilder _lane;
	private BallController _ball;
	private BallCamera _camera;
	private int _spawnCount;   // walking bowlers dropped so far, to fan them across the settee

	// --- Approach-B throw networking (the active bowler is authoritative for their own turn) -------
	private readonly List<int> _localBalls = new(); // bowler-side: the balls thrown THIS frame, for the rules
	private int _servedTurn = -1;    // the TurnSerial we last began a turn for (-1 = none yet this client)
	private bool _ballNetReady;      // host: have we tagged the networked ball Takeover yet?
	private TerryAvatar _localAvatar;
	private TerryAvatar LocalAvatar =>
		(_localAvatar.IsValid() ? _localAvatar : (_localAvatar = Scene.GetAllComponents<TerryAvatar>().FirstOrDefault( a => a.IsValid() && !a.IsProxy )));

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
		// Auto-host so the scene is playable solo (you become the host + first bowler) and ready to
		// accept joiners. If the scene was loaded BY joining someone else's lobby, a session is
		// already active and we leave it alone.
		if ( !Networking.IsActive )
			Networking.CreateLobby( new Sandbox.Network.LobbyConfig() );

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
		if ( Networking.IsHost )
		{
			Phase = GamePhase.Lobby;
			PublishScores();
		}
	}

	// The on-screen HUD is no longer built here. It's a single client-local, NON-networked object
	// (see LocalHud) so the host's music / panel state can't leak onto clients, and it follows whatever
	// lane the local player has joined rather than belonging to one director.

	// --- Network roster ----------------------------------------------------------------
	// INetworkListener fires on the HOST. Every player who joins (the local host included) gets a
	// walking Terry on the settee; leaving despawns it. Whether they actually BOWL is a separate
	// opt-in (TerryAvatar.Joined), locked into the scorecard when the host presses START.

	/// <summary>Host-side: a player finished joining — drop them a walking Terry on the settee.</summary>
	public void OnActive( Connection channel ) => SpawnAvatar( channel );

	/// <summary>Host-side: a player left — remove their Terry (and drop them from a game in progress).</summary>
	public void OnDisconnected( Connection channel )
	{
		foreach ( var av in Scene.GetAllComponents<TerryAvatar>().ToList() )
			if ( av.IsValid() && av.GameObject.Network.Owner?.Id == channel.Id )
				av.GameObject.Destroy();

		// If the player who left was the one UP on this lane, their orphaned rack would otherwise sit
		// there forever. Advance the turn so the host reclaims the ball + rack and the next bowler is up.
		if ( Networking.IsHost && Phase == GamePhase.Playing
			&& channel is not null && channel.Id == ActiveBowlerId )
			AdvanceTurn();
	}

	/// <summary>Build one player's walking-Terry avatar (a PlayerController Citizen) at a settee spot
	/// and hand network ownership to them. Host only — the spawn replicates to every client.</summary>
	private void SpawnAvatar( Connection channel )
	{
		if ( !Networking.IsHost ) return;

		var go = new GameObject( true, $"Terry_{channel.DisplayName}" )
		{
			WorldPosition = NextSpawnPosition(),
			WorldRotation = _lane.IsValid() ? _lane.SetteeFaceRotation : Rotation.Identity, // face down the lane from the settee
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

	/// <summary>Fan spawned bowlers across the settee width so they don't pile on one spot.</summary>
	private Vector3 NextSpawnPosition()
	{
		var center = _lane.IsValid() ? _lane.SetteeFloorCenter : Vector3.Zero;
		var half = _lane.IsValid() ? _lane.SetteeHalfWidthUnits : 200f;
		var slots = Math.Max( 1, MaxPlayers );
		var slot = _spawnCount++;
		var t = slots > 1 ? (slot % slots) / (float)(slots - 1) : 0.5f; // 0..1 across the seats, then wraps
		var y = MathX.Lerp( -half * 0.7f, half * 0.7f, t );
		return center + new Vector3( 0f, y, 0f );
	}

	// --- Lobby controls (driven by the lane controls panel) -----------------------------
	// Join/Leave is each client toggling its OWN avatar's (owner-controlled) Joined flag directly —
	// no RPC. Start/Stop are host-only and called straight on the director by the host's own controls
	// panel (clients don't show those buttons), so there's no instance-RPC on the scene object.

	/// <summary>Host: lock the joined players into a fresh scorecard and tip off frame 1.</summary>
	public void StartGame()
	{
		if ( !Networking.IsHost || Phase == GamePhase.Playing ) return;

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
		// (BeginTurn) the moment their spotlight (ActiveBowlerId) lands.
		HostRackForNextBowler();
		SetActiveBowler();
		PublishScores();
		Log.Info( $"[terryball][game] START — {_players.Count} bowler(s), frame 1" );
	}

	/// <summary>Host: clear the scorecard and drop everyone back to the lobby (walking the settee).</summary>
	public void StopGame()
	{
		if ( !Networking.IsHost ) return;

		Phase = GamePhase.Lobby;
		_players.Clear();
		_rosterIds.Clear();
		_current = 0;
		_frame = 0;
		_gameOver = false;
		ActiveBowlerId = Guid.Empty;
		State = RoundState.Rolling;

		foreach ( var av in Scene.GetAllComponents<TerryAvatar>() )
			if ( av.IsValid() ) av.Bowling = false;

		if ( _camera.IsValid() ) _camera.Watching = false;
		HostReclaimThrowOwnership(); // take the ball + rack back before the host re-racks/parks them
		FullRack();
		PlaceBallHome();
		PublishScores();
		Log.Info( "[terryball][game] STOP — back to the lobby" );
	}

	/// <summary>Point the spotlight at the current roster player: publish their connection id and flip
	/// exactly their avatar into Bowling (the rest walk/spectate). Host only.</summary>
	private void SetActiveBowler()
	{
		TurnSerial++; // a new turn is starting — even if it's the same bowler as last turn (see TurnSerial)
		ActiveBowlerId = (_current >= 0 && _current < _rosterIds.Count) ? _rosterIds[_current] : Guid.Empty;
		foreach ( var av in Scene.GetAllComponents<TerryAvatar>() )
			if ( av.IsValid() )
				av.Bowling = av.GameObject.Network.Owner?.Id == ActiveBowlerId && ActiveBowlerId != Guid.Empty;
	}

	/// <summary>Host: mirror the authoritative scoring state into the synced (FromHost) fields every
	/// client renders from. Called after anything that changes the cards or whose turn it is.</summary>
	private void PublishScores()
	{
		if ( !Networking.IsHost ) return;

		RosterNames.Clear();
		RosterScores.Clear();
		foreach ( var p in _players )
		{
			RosterNames.Add( p.Name );
			RosterScores.Add( EncodeFrames( p.Frames ) );
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

	protected override void OnUpdate()
	{
		if ( !_lane.IsValid() || !_ball.IsValid() ) return;

		// Re-assert every frame: the engine swaps the PhysicsWorld after OnStart, so a one-time set
		// silently reverted to 1. This keeps the cascade properly substepped on the live world.
		Scene.PhysicsWorld.SubSteps = PhysicsSubSteps;

		// Host: bring the shared, networked rack online (once the lane is built) and flag the ball
		// Takeover so the active bowler can grab it for their turn. Both are idempotent.
		if ( Networking.IsHost )
		{
			_lane.HostSpawnPins();
			if ( !_ballNetReady && _ball.GameObject.Network.Active )
			{
				_ball.GameObject.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
				_ballNetReady = true;
			}
		}

		// Host keyboard P was a quick START/STOP toggle for solo testing. Commented out for now.
		// if ( Networking.IsHost && Input.Keyboard.Pressed( "P" ) )
		// {
		// 	if ( Phase == GamePhase.Playing ) StopGame(); else StartGame();
		// 	return;
		// }

		if ( Phase != GamePhase.Playing )
			return;

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

				if ( pinsStill && ballStill )
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

		var turnDone = DecideLocal();
		LocalAvatar?.ReportBall( knocked, PinDownBits, turnDone ); // host books it onto the card

		if ( turnDone )
		{
			// Hand back: park the ball and wait for the host to spotlight the next bowler.
			_ball.ControlsLocked = true;
			State = RoundState.Rolling;
			if ( _camera.IsValid() ) _camera.Watching = false;
		}
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

	// HOST: in the gap between turns the host owns the ball + rack. Take them back from the bowler who
	// just finished, drop a clean full deck and park the ball, so the next bowler grabs a stable,
	// already-correct rack (no contested writes, no leftover pins). No-op outside a live session.
	private void HostRackForNextBowler()
	{
		if ( !Networking.IsHost ) return;
		HostReclaimThrowOwnership();
		FullRack();
		PlaceBallHome();
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
		BeginRolling();
	}

	private void EndGame()
	{
		_gameOver = true;
		State = RoundState.Rolling;
		if ( _camera.IsValid() ) _camera.Watching = false;
		_ball.ControlsLocked = true; // park the ball; STOP returns everyone to the lobby
		HostReclaimThrowOwnership(); // own the ball + rack so the host can park/hold them on the final board
		PlaceBallHome();

		// Everyone exits the ball and is back to walking; the final board stays up until STOP.
		ActiveBowlerId = Guid.Empty;
		foreach ( var av in Scene.GetAllComponents<TerryAvatar>() )
			if ( av.IsValid() ) av.Bowling = false;

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
		BeginRolling();  // ball home, controls live, camera on the ball
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

	// HOST: pull the ball + rack back to the host between/after turns (lobby, game over), so the host
	// can park the ball and re-rack on objects it owns rather than fighting the last bowler's ownership.
	private void HostReclaimThrowOwnership()
	{
		if ( !Networking.IsHost ) return;
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

	/// <summary>Host side (called from the bowler's ReportBall RPC): book a settled ball's pinfall onto
	/// the active player's card, reflect the leave on the host's own panels, and rotate when the
	/// bowler says their turn is done.</summary>
	public void HostBookBall( int knocked, int downMask, bool turnDone )
	{
		if ( !Networking.IsHost || Phase != GamePhase.Playing ) return;

		if ( _current >= 0 && _current < _players.Count && _frame >= 0 && _frame < 10 )
			_players[_current].Frames[_frame].Add( knocked );
		PinDownBits = downMask;
		PublishScores();

		if ( turnDone )
			AdvanceTurn();
	}

	/// <summary>Host side (called from the bowler's RequestLeaveBall RPC): the active bowler hit the
	/// escape hatch — end their turn so the game moves on and they drop out of the ball. Recovers from a
	/// wedged turn without anyone having to restart the game.</summary>
	public void HostLeaveBall( Connection caller )
	{
		if ( !Networking.IsHost || Phase != GamePhase.Playing ) return;
		if ( caller is null || caller.Id != ActiveBowlerId ) return; // only the player who's up can bail
		Log.Info( "[terryball][game] bowler bailed out of the ball — advancing the turn" );
		AdvanceTurn();
	}

	// Drop every pin back onto its spot, enabled, upright and dead still — a clean rack.
	private void FullRack()
	{
		_sweptPins.Clear();
		foreach ( var p in _lane.Pins )
			RespotPin( p );
	}

	// Sweep the pins this ball knocked down (disable them so they're off the deck and can't foul
	// the next ball) and respot the ones still standing back onto their exact spots.
	private void SweepAndRespot()
	{
		foreach ( var s in _statuses )
		{
			var p = _lane.Pins.FirstOrDefault( pp => pp.Number == s.Number );
			if ( !p.Object.IsValid() ) continue;
			if ( s.Down )
			{
				_sweptPins.Add( p.Number );
				p.Object.Enabled = false; // off the deck
			}
			else
			{
				RespotPin( p ); // nudged-but-standing pin returns to its home spot
			}
		}
	}

	// Place one pin on its home spot, enabled, upright and motionless.
	private void RespotPin( LaneBuilder.PinPlacement p )
	{
		if ( !p.Object.IsValid() ) return;
		p.Object.Enabled = true;
		p.Object.WorldPosition = p.Home;
		p.Object.WorldRotation = Rotation.Identity;
		var rb = p.Object.Components.Get<Rigidbody>();
		if ( rb.IsValid() )
		{
			rb.Velocity = Vector3.Zero;
			rb.AngularVelocity = Vector3.Zero;
		}
	}

	// Drop the ball on its run-up spot, upright and dead still.
	private void PlaceBallHome()
	{
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
}
