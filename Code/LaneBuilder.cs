using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// Builds a bowling lane at runtime, scaled so the ball reads as a real bowling ball.
///
/// The ball's actual size in units is treated as <see cref="BallDiameterInches"/> (8.5"),
/// which gives a units-per-inch scale used for every real dimension below: two cube walls
/// a lane-width apart, and 10 cube "pins" (each a Rigidbody) arranged in the regulation
/// triangle at the foul-line-to-head-pin distance — for the ball to crash into.
///
/// (s&box is 1 unit ≈ 1 inch, but we derive the scale from the ball so it stays correct
/// even if the ball's Radius is retuned.)
/// </summary>
public sealed class LaneBuilder : Component
{
	[Property, Group( "Scale" )] public float BallDiameterInches { get; set; } = 8.5f;
	[Property, Group( "Lane" )] public float LaneWidthInches { get; set; } = 41.5f;
	[Property, Group( "Lane" )] public float LaneLengthInches { get; set; } = 720f; // foul line → head pin (60 ft)
	[Property, Group( "Walls" )] public float WallHeight { get; set; } = 150f;       // units, as requested
	[Property, Group( "Walls" )] public float WallThickness { get; set; } = 40f;
	[Property, Group( "Walls" )] public Color WallColor { get; set; } = new Color( 0.55f, 0.40f, 0.25f, 1f );
	[Property, Group( "Pins" )] public float PinSpacingInches { get; set; } = 12f;   // pin centres
	[Property, Group( "Pins" )] public float PinWidthInches { get; set; } = 5f;
	[Property, Group( "Pins" )] public float PinHeightInches { get; set; } = 15f;
	[Property, Group( "Pins" )] public Color PinColor { get; set; } = new Color( 0.95f, 0.95f, 0.95f, 1f );

	private bool _built;

	protected override void OnEnabled()
	{
		if ( _built ) return;
		_built = true;

		// Treat the actual ball as 8.5" to get a units-per-inch scale for everything else.
		var ball = Scene.GetAllComponents<BallController>().FirstOrDefault();
		var ballDiameterUnits = ball.IsValid() ? ball.Radius * 2f : 200f;
		var u = ballDiameterUnits / BallDiameterInches; // units per inch

		var laneHalf = LaneWidthInches * u * 0.5f;
		var headPinX = LaneLengthInches * u;
		var pinSpace = PinSpacingInches * u;
		var rowDepth = pinSpace * 0.8660254f; // 12" centres, rows step back by 12·cos(30°)
		var pinW = PinWidthInches * u;
		var pinH = PinHeightInches * u;

		// Walls: a lane-width gap apart, spanning from behind the ball to past the pins.
		var wallStartX = -1000f;
		var wallEndX = headPinX + rowDepth * 3f + 1500f;
		var wallLen = wallEndX - wallStartX;
		var wallCenterX = (wallStartX + wallEndX) * 0.5f;
		var wallY = laneHalf + WallThickness * 0.5f; // inner face sits at the lane edge

		MakeBox( "WallLeft", new Vector3( wallCenterX, -wallY, WallHeight * 0.5f ),
			new Vector3( wallLen, WallThickness, WallHeight ), WallColor, isDynamic: false );
		MakeBox( "WallRight", new Vector3( wallCenterX, wallY, WallHeight * 0.5f ),
			new Vector3( wallLen, WallThickness, WallHeight ), WallColor, isDynamic: false );

		// 10 pins in the regulation triangle: head pin nearest the bowler, rows of 1/2/3/4
		// stepping away down the lane, each row centred across the lane.
		var n = 1;
		for ( var r = 0; r < 4; r++ )
		{
			var px = headPinX + r * rowDepth;
			for ( var i = 0; i <= r; i++ )
			{
				var py = (i - r * 0.5f) * pinSpace;
				MakeBox( $"Pin{n}", new Vector3( px, py, pinH * 0.5f ),
					new Vector3( pinW, pinW, pinH ), PinColor, isDynamic: true );
				n++;
			}
		}
	}

	// Collider on the uniformly-scaled root (s&box freezes physics if a BoxCollider rides a
	// non-uniform scale); visual on a non-uniformly-scaled child driven by model bounds.
	private void MakeBox( string name, Vector3 worldPos, Vector3 size, Color tint, bool isDynamic )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;

		var col = go.Components.GetOrCreate<BoxCollider>();
		col.Scale = size;

		if ( isDynamic )
			go.Components.GetOrCreate<Rigidbody>();

		var visual = new GameObject( true, name + "Visual" ) { Parent = go };
		var mr = visual.Components.GetOrCreate<ModelRenderer>();
		mr.Model = Model.Load( "models/dev/box.vmdl" );
		mr.Tint = tint;

		var b = mr.Model.Bounds.Size;
		visual.LocalScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );
	}
}
