using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// Drives the settee's right-hand "camera feed" screen. It spawns a second camera that chases the
/// active ball down-lane and renders to <see cref="Feed"/> (a render-target texture); the
/// <c>MonitorFeed</c> panel paints that texture onto the in-world screen.
///
/// It runs on EVERY client as a local re-render of the LOCAL ball — which is the bowler's real ball
/// on their machine and the streamed copy on spectators — so every settee shows the bowler's-eye
/// view without streaming any video. (A chase cam, not a literal mirror of the bowler's look angles.)
/// </summary>
public sealed class LaneMonitorCamera : Component
{
	[Property] public int Width { get; set; } = 1280;
	[Property] public int Height { get; set; } = 720;
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
