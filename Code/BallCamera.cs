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
	[Property, Range( -89f, 89f, 1f )] public float MinPitch { get; set; } = -5f;
	/// <summary>Highest the camera can pitch (look up), in degrees.</summary>
	[Property, Range( -89f, 89f, 1f )] public float MaxPitch { get; set; } = 15f;

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

	private BallController _ball;
	private LaneBuilder _lane;
	private float _yaw;
	private float _pitch = 15f;
	private float _blend; // 0 = on the ball, 1 = pin-deck watch vantage

	protected override void OnEnabled()
	{
		var lane = Lane.Of( this );
		_ball = lane?.Ball;
		_lane = lane?.Builder;
	}

	protected override void OnUpdate()
	{
		// Only ride the ball when the local player is the bowler who's up ON THIS LANE. Otherwise this
		// client is walking its Terry around the settee, and its PlayerController owns the camera — so
		// we bow out entirely and don't fight it for Scene.Camera.
		var lane = Lane.Of( this );
		var director = lane?.Director;
		if ( director is not null && !director.LocalIsBowling ) return;

		if ( !_ball.IsValid() )
		{
			_ball = lane?.Ball;
			if ( !_ball.IsValid() ) return;
		}
		if ( !_lane.IsValid() )
			_lane = lane?.Builder;

		// Freeze look input while watching OR while the chat box / music board is open (so typing or
		// clicking the panel doesn't spin the camera); otherwise yaw/pitch follow the mouse as normal.
		if ( !Watching && !ChatPanel.IsOpen && !MusicScreen.IsOpen )
		{
			var look = Input.AnalogLook; // sign + sensitivity already applied
			_yaw += look.yaw;
			_pitch = ( _pitch - look.pitch ).Clamp( MinPitch, MaxPitch );
		}

		// --- Play framing: third-person follow behind the ball, horizon-locked. ----------------
		var playRot = Rotation.From( _pitch, _yaw, 0f );
		var playPos = _ball.WorldPosition + Vector3.Up * Height - playRot.Forward * Distance;

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
}
