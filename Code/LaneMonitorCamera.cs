using Sandbox;
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

		// Mirror the active bowler's REAL camera, if someone's up: their owner publishes its transform on
		// their avatar (TerryAvatar.BowlCamPos/Rot), so every settee monitor shows the bowler's eye view —
		// it pans/pitches with their aim and follows their throw, not a fixed chase. Identical on host and
		// spectator because both read the same synced transform.
		var bowler = Scene.GetAllComponents<TerryAvatar>().FirstOrDefault( a => a.IsValid() && a.Bowling );
		if ( bowler is not null )
		{
			_cam.WorldPosition = bowler.BowlCamPos;
			_cam.WorldRotation = bowler.BowlCamRot;
			return;
		}

		// Nobody up (lobby / between turns): fall back to a chase framing of the local ball.
		if ( !_ball.IsValid() || !_lane.IsValid() )
		{
			var lane = Lane.Of( this );
			if ( !_ball.IsValid() ) _ball = lane?.Ball;
			if ( !_lane.IsValid() ) _lane = lane?.Builder;
		}
		if ( !_ball.IsValid() ) return;

		// Chase cam: behind the ball (-x) and above, looking down-lane toward the pins.
		var ball = _ball.WorldPosition;
		var camPos = ball + new Vector3( -Back, 0f, Up );
		var look = _lane.IsValid() ? _lane.PinDeckWorldCenter : ball + Vector3.Forward * 2000f;
		_cam.WorldPosition = camPos;
		_cam.WorldRotation = Rotation.LookAt( (look - camPos).Normal, Vector3.Up );
	}
}
