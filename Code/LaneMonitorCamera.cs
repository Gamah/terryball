using Sandbox;
using System;
using System.Linq;

namespace Terryball;

/// <summary>
/// Drives the settee's right-hand "camera feed" screen. It spawns a second camera that chases the
/// active ball down-lane and renders to <see cref="Feed"/> (a render-target texture); the
/// <c>MonitorFeed</c> panel paints that texture onto the in-world screen.
///
/// It runs on EVERY client. While someone's up it MIRRORS the active bowler's real camera (their owner
/// publishes the transform on their avatar, <see cref="TerryAvatar.BowlCamPos"/>), so every settee shows
/// the bowler's actual eye view — panning and pitching with their aim — without streaming any video.
/// Between turns it falls back to a chase framing of the local ball.
/// </summary>
public sealed class LaneMonitorCamera : Component
{
	[Property] public int Width { get; set; } = 1920;
	[Property] public int Height { get; set; } = 1080;
	[Property] public float Back { get; set; } = 700f;  // how far behind the ball the cam sits (units)
	[Property] public float Up { get; set; } = 260f;    // and how far above
	[Property] public float Fov { get; set; } = 70f;
	/// <summary>Far clip for the feed camera. The default CameraComponent ZFar (~10000) cut off the
	/// back of the lane on the settee screen; match the main camera's reach so the whole lane renders.</summary>
	[Property] public float ZFar { get; set; } = 60000f;

	/// <summary>How fast the feed eases toward its target each second. Smooths the stepped network values
	/// a spectator receives for the bowler's camera, so the feed glides instead of snapping/jittering.</summary>
	[Property, Group( "Follow" )] public float FollowLerp { get; set; } = 12f;
	/// <summary>Pull the feed back this far along the bowler's view direction, so the monitor shows a
	/// slightly wider shot than the bowler's exact (cramped) eye position.</summary>
	[Property, Group( "Follow" )] public float Pullback { get; set; } = 350f;

	/// <summary>The live render target the settee feed screen displays.</summary>
	public Texture Feed { get; private set; }

	private CameraComponent _cam;
	private BallController _ball;
	private LaneBuilder _lane;

	protected override void OnStart()
	{
		Feed = Texture.CreateRenderTarget( "lane-feed", ImageFormat.RGBA8888, new Vector2( Width, Height ) );

		var go = new GameObject( true, "MonitorCam" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		_cam = go.Components.Create<CameraComponent>();
		_cam.IsMainCamera = false;          // render to our texture only; never take over the screen
		_cam.RenderTarget = Feed;
		_cam.FieldOfView = Fov;
		_cam.ZFar = ZFar;
		_cam.BackgroundColor = Color.Black;

		var lane = Lane.Of( this );
		_ball = lane?.Ball;
		_lane = lane?.Builder;
	}

	protected override void OnUpdate()
	{
		if ( !_cam.IsValid() ) return;

		// Resolve the feed's TARGET transform: the active bowler's eye view if someone's up, else a chase
		// of the ball. Then ease toward it (below) so the feed never snaps/jitters.
		var haveTarget = TryGetTarget( out var targetPos, out var targetRot );
		if ( !haveTarget ) return;

		var t = Math.Min( 1f, FollowLerp * Time.Delta );
		_cam.WorldPosition = Vector3.Lerp( _cam.WorldPosition, targetPos, t );
		_cam.WorldRotation = Rotation.Slerp( _cam.WorldRotation, targetRot, t );
	}

	private bool TryGetTarget( out Vector3 pos, out Rotation rot )
	{
		pos = default; rot = default;

		// When WE'RE the one bowling, read the live local main camera directly — no network lag, dead
		// smooth. Otherwise mirror the active bowler's synced camera transform (every spectator's feed
		// shows the bowler's eye view). Either way, pull back a touch for a less cramped shot.
		var dir = Lane.Of( this )?.Director;
		if ( dir is not null && dir.LocalIsBowling && Scene.Camera.IsValid() )
		{
			rot = Scene.Camera.WorldRotation;
			pos = Scene.Camera.WorldPosition - rot.Forward * Pullback;
			return true;
		}

		var bowler = Scene.GetAllComponents<TerryAvatar>().FirstOrDefault( a => a.IsValid() && a.Bowling );
		if ( bowler is not null && bowler.BowlCamPos != Vector3.Zero )
		{
			rot = bowler.BowlCamRot;
			pos = bowler.BowlCamPos - rot.Forward * Pullback;
			return true;
		}

		// Nobody up (lobby / between turns): chase framing of the local ball.
		if ( !_ball.IsValid() || !_lane.IsValid() )
		{
			var lane = Lane.Of( this );
			if ( !_ball.IsValid() ) _ball = lane?.Ball;
			if ( !_lane.IsValid() ) _lane = lane?.Builder;
		}
		if ( !_ball.IsValid() ) return false;

		var ball = _ball.WorldPosition;
		pos = ball + new Vector3( -Back, 0f, Up );
		var look = _lane.IsValid() ? _lane.PinDeckWorldCenter : ball + Vector3.Forward * 2000f;
		rot = Rotation.LookAt( (look - pos).Normal, Vector3.Up );
		return true;
	}
}
