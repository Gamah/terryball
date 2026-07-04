using Sandbox;
using System;
using System.Linq;

namespace Terryball;

/// <summary>
/// Bowling camera. During the roll it RIDES WITH THE BALL — a third-person follow cam behind it,
/// horizon-locked (never rolls with the ball's spin), so "forward" is always down-lane and the
/// camera-relative steering in <see cref="BallController"/> stays predictable. The mouse yaws
/// around the ball and pitches to look up/down.
///
/// The camera only LEAVES the ball once the throw crosses the throw line: the director sets
/// <see cref="Watching"/>, and the camera eases off to a vantage just behind and above the pin
/// deck to watch the hit (look input frozen). On reset the director clears Watching and the camera
/// eases straight back onto the ball at the run-up — so it's never detached from the player except
/// during the watched hit.
/// </summary>
public sealed class BallCamera : Component
{
	[Property] public float Distance { get; set; } = 700f;
	[Property] public float Height { get; set; } = 60f;
	/// <summary>Lowest the camera can pitch (look down), in degrees.</summary>
	[Property, Range( -89f, 89f ), Step( 1f )] public float MinPitch { get; set; } = -5f;
	/// <summary>Highest the camera can pitch (look up), in degrees.</summary>
	[Property, Range( -89f, 89f ), Step( 1f )] public float MaxPitch { get; set; } = 15f;

	// --- Watch mode -------------------------------------------------------------------------
	// Once the ball crosses the throw line the camera blends OFF the ball to a vantage behind and
	// above the pin deck for a close overview of the strike. We blend a 0→1 weight so play tracks
	// the ball tightly (no lag) and only the entry/exit to watch eases.
	[Property, Group( "Watch" )] public float WatchBack { get; set; } = 1400f;  // behind the deck
	[Property, Group( "Watch" )] public float WatchHeight { get; set; } = 700f; // and up, for the overview
	[Property, Group( "Watch" )] public float WatchLerp { get; set; } = 4f;      // blend rate (per second)

	/// <summary>Driven by the round director: blend off the ball to the pin-deck overview and stop
	/// reading look. Cleared on reset, so the camera blends back onto the ball.</summary>
	public bool Watching { get; set; }

	/// <summary>Seconds to ease the camera across the hand-off between the walking-Terry view and the
	/// in-ball view (the scoop/drop), instead of snapping in one frame.</summary>
	[Property, Group( "Transition" )] public float TransitionSeconds { get; set; } = 0.45f;

	/// <summary>Fixed pitch used while the bowler is lining up a guided (Aim-mode) throw — the camera is locked
	/// to a steady down-lane shot then, so it doesn't read the mouse or follow the ball's lateral shuffle.</summary>
	[Property, Group( "Aim" )] public float AimPitch { get; set; } = 10f;
	/// <summary>Camera height while lining up a guided throw — raised above the normal follow height so the
	/// aim arrow floating down-lane ahead of the ball clears the top of the ball.</summary>
	[Property, Group( "Aim" )] public float AimHeight { get; set; } = 150f;
	/// <summary>Rate (per second) the camera's lateral anchor eases onto the ball's line while aiming and after
	/// the throw commits — a light, smooth follow so the ball's motion and the release don't snap the view.</summary>
	[Property, Group( "Aim" )] public float AimLockLerp { get; set; } = 6f;

	private BallController _ball;
	private LaneBuilder _lane;

	// The client-local guided-throw driver (single instance on the local HUD). We read its LiningUp flag to
	// lock the camera while a throw is being aimed. Resolved lazily and re-resolved if it goes invalid.
	private ThrowAimer _aimer;
	private ThrowAimer Aimer => _aimer.IsValid() ? _aimer : (_aimer = Scene.GetAllComponents<ThrowAimer>().FirstOrDefault());

	// Eased lateral follow anchor: the ball's own Y, followed with a light ease while aiming (and after release)
	// so the hand-positioned line and the throw don't snap the view sideways.
	private float _followY;
	private bool _followYInit;
	private float _yaw;
	private float _pitch = 15f;
	private float _blend; // 0 = on the ball, 1 = pin-deck watch vantage

	// Terry-cam ↔ ball-cam hand-off smoothing. Whoever owns the camera this frame (PlayerController when
	// walking, this when scooped in) has already written Scene.Camera by OnPreRender; we ease across the
	// jump that happens the moment ownership flips (Carried edge).
	private bool _wasInBallUpdate;   // rising-edge tracker in OnUpdate, for adopting the view direction
	private bool _hasApplied, _wasInBall, _blendActive;
	private float _blendElapsed;
	private Vector3 _appliedPos, _blendFromPos;
	private Rotation _appliedRot, _blendFromRot;

	// The camera is a SINGLE, client-local view (one main camera for the whole alley), not one per lane —
	// N main cameras would fight over the screen. It follows whichever lane the LOCAL player is on, so it
	// resolves its lane dynamically rather than by its parent. Falls back to the single lane before an
	// avatar exists.
	private Lane ActiveLane => Lane.ForLocalPlayer( Scene );

	protected override void OnEnabled()
	{
		var lane = ActiveLane;
		_ball = lane?.Ball;
		_lane = lane?.Builder;
	}

	protected override void OnUpdate()
	{
		// Only ride the ball when the local player is actually scooped INSIDE it on their active lane.
		// Otherwise — walking, OR the bowler mid-crane (claw flying out to fetch them) — the PlayerController
		// owns the camera and keeps it on their own Terry, so we bow out and don't fight for Scene.Camera.
		var lane = ActiveLane;
		var director = lane?.Director;
		var localAv = TerryAvatar.Local( Scene );
		var inBall = director is not null && director.LocalIsBowling && (localAv?.Carried ?? false);

		// Just got scooped in: adopt the walking camera's current aim so the ball-cam picks up pointing
		// exactly where Terry was looking (no swing). The camera still holds that pose now — PlayerController
		// is suspended this frame and we haven't driven it yet.
		if ( inBall && !_wasInBallUpdate )
		{
			var a = WorldRotation.Angles();
			_yaw = a.yaw;
			_pitch = a.pitch.Clamp( MinPitch, MaxPitch );
		}
		_wasInBallUpdate = inBall;

		// Only drive the camera while the local player is actually riding the ball. Any other time — walking,
		// spectating, waiting to be picked up — the PlayerController owns the camera and follows the player,
		// so we must NOT touch it (otherwise a walking player is stranded looking at the parked ball).
		if ( !inBall ) return;

		// Refresh from the active lane every frame (it changes as the player moves between lanes), not just
		// when the cached refs go invalid — otherwise a still-valid old ball would keep the camera on it.
		_ball = lane?.Ball;
		_lane = lane?.Builder;
		if ( !_ball.IsValid() ) return;

		// Freeze look input while watching OR while the chat box / music board is open (so typing or
		// clicking the panel doesn't spin the camera); otherwise yaw/pitch follow the mouse as normal.
		if ( !Watching && !ChatPanel.IsOpen && !MusicScreen.IsOpen )
		{
			var look = Input.AnalogLook; // sign + sensitivity already applied
			_yaw += look.yaw;
			_pitch = ( _pitch - look.pitch ).Clamp( MinPitch, MaxPitch );
		}

		// --- Play framing: third-person follow behind the ball, horizon-locked. ----------------
		// While a guided (Aim-mode) throw is being lined up, hold a steady shot straight down the lane from
		// behind the ball — the camera yaw is pinned down-lane (the mouse instead slides the ball during LINE),
		// and the lateral framing below tracks the ball's line the whole time.
		var liningUp = (Aimer?.LiningUp ?? false) && _lane.IsValid();
		if ( liningUp )
		{
			_yaw = 0f;         // straight down-lane (+X)
			_pitch = AimPitch;
		}

		// Lateral follow anchor: the camera tracks the ball's own Y. This light easing is an AIM-MODE behaviour
		// only — while lining up (and until it re-converges after the aimed release) the ball is hand-positioned
		// side to side / parked, so the aiming and the release slide onto its line rather than snapping. In free
		// hamster-ball drive it must follow the ball TIGHTLY: the eased Y used to lag the ball as it darted
		// across the lane, leaving the camera sat on the lane centre-line instead of behind the ball.
		var aimFollow = director?.AimMode ?? false;
		var tightY = _ball.WorldPosition.y;
		if ( !_followYInit ) { _followY = tightY; _followYInit = true; }
		_followY = ( aimFollow && (liningUp || MathF.Abs( _followY - tightY ) >= 2f) )
			? MathX.Lerp( _followY, tightY, MathF.Min( 1f, AimLockLerp * Time.Delta ) )
			: tightY;

		var playRot = Rotation.From( _pitch, _yaw, 0f );
		var anchor = new Vector3( _ball.WorldPosition.x, _followY, _ball.WorldPosition.z );
		// Sit higher while lining up so the down-lane aim arrow clears the top of the ball.
		var camHeight = liningUp ? AimHeight : Height;
		var playPos = anchor + Vector3.Up * camHeight - playRot.Forward * Distance;

		// --- Watch framing: behind + above the pin deck, looking at it. -------------------------
		var watchPos = playPos;
		var watchRot = playRot;
		if ( _lane.IsValid() )
		{
			var deck = _lane.PinDeckWorldCenter;
			watchPos = new Vector3( deck.x - WatchBack, deck.y, deck.z + WatchHeight );
			var aim = Rotation.LookAt( (deck - watchPos).Normal, Vector3.Up ).Angles();
			watchRot = Rotation.From( aim.pitch, aim.yaw, 0f ); // horizon-locked
		}

		// Blend toward/away from the watch vantage. At blend 0 the camera tracks the ball tightly;
		// only the transition into/out of watch eases.
		_blend = MathX.Lerp( _blend, Watching ? 1f : 0f, MathF.Min( 1f, WatchLerp * Time.Delta ) );

		WorldPosition = Vector3.Lerp( playPos, watchPos, _blend );
		WorldRotation = Rotation.Slerp( playRot, watchRot, _blend );
	}

	// Runs after every OnUpdate (so after whichever component owns the camera this frame has set it), and
	// every frame regardless of the OnUpdate bow-out above — so it can ease BOTH directions of the hand-off.
	protected override void OnPreRender()
	{
		var targetPos = WorldPosition;
		var targetRot = WorldRotation;

		var director = ActiveLane?.Director;
		var localAv = TerryAvatar.Local( Scene );
		var inBall = director is not null && director.LocalIsBowling && (localAv?.Carried ?? false);

		if ( !_hasApplied )
		{
			_appliedPos = targetPos;
			_appliedRot = targetRot;
			_wasInBall = inBall;
			_hasApplied = true;
		}

		// Ownership just flipped (scoop or drop): start easing from whatever we were actually showing toward
		// the new owner's view. We hold _blendFrom and chase the LIVE target, so it's robust to which
		// component happened to update first this frame.
		if ( inBall != _wasInBall )
		{
			_blendActive = true;
			_blendElapsed = 0f;
			_blendFromPos = _appliedPos;
			_blendFromRot = _appliedRot;
		}
		_wasInBall = inBall;

		// Neither riding the ball nor mid hand-off → the walking PlayerController owns the camera; leave it
		// entirely alone. (Without this the passthrough write below would fight it and strand the view.) But
		// TRACK what it's showing: a scoop that fires next frame eases _blendFrom = _appliedPos, so this must
		// be the live walk view — not a stale pose left over from our last throw, which would snap the camera
		// hard to that old vantage on the first scoop frame before easing in.
		if ( !inBall && !_blendActive )
		{
			_appliedPos = targetPos;
			_appliedRot = targetRot;
			return;
		}

		if ( _blendActive )
		{
			_blendElapsed += Time.Delta;
			var dur = MathF.Max( 0.01f, TransitionSeconds );
			var k = (_blendElapsed / dur).Clamp( 0f, 1f );
			k = k * k * (3f - 2f * k); // smoothstep
			_appliedPos = Vector3.Lerp( _blendFromPos, targetPos, k );
			_appliedRot = Rotation.Slerp( _blendFromRot, targetRot, k );
			if ( _blendElapsed >= dur ) _blendActive = false;
		}
		else
		{
			_appliedPos = targetPos;
			_appliedRot = targetRot;
		}

		WorldPosition = _appliedPos;
		WorldRotation = _appliedRot;
	}
}
