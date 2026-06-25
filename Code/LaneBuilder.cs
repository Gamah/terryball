using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Builds a full bowling lane at runtime — not just a lane-width strip with walls, but a
/// raised lane platform with a roll-up ramp, a regulation 15 ft approach, a foul line, the
/// 60 ft lane proper marked out in boards and targeting arrows, gutters either side with low
/// barrier curbs beyond them, a recessed pit behind the pins, and the ten lathed pins.
///
/// Everything is sized in real bowling inches and multiplied by a units-per-inch scale that
/// is itself derived from the ball: the ball's actual diameter in units is treated as
/// <see cref="BallDiameterInches"/> (8.5"), so the whole lane stays correctly scaled to the
/// ball even if the ball's Radius is retuned. (Per the stylised-scale note, the platform
/// height / ramp / pit are tuned for feel rather than literal millimetres.)
///
/// The pin Model is a surface-of-revolution: a smooth render mesh plus a stack of convex-hull
/// frustums for collision, so the physics engine derives a realistic low centre of mass and
/// inertia tensor straight from the true solid — no hand-tuned mass-centre needed.
///
/// Coordinate scheme (units): the ball spawns at world origin on the floor (top z = 0); the
/// lane is laid out ahead of it in +x with the ramp bottom resting on the floor, rising to the
/// platform top at z = <c>H</c>. The foul line is the x datum for every real dimension below.
/// </summary>
public sealed class LaneBuilder : Component
{
	[Property, Group( "Scale" )] public float BallDiameterInches { get; set; } = 8.5f;

	[Property, Group( "Lane" )] public float LaneWidthInches { get; set; } = 41.5f;
	[Property, Group( "Lane" )] public float LaneLengthInches { get; set; } = 720f; // foul line → head pin (60 ft)
	[Property, Group( "Lane" )] public int BoardCount { get; set; } = 39;            // a regulation lane is 39 boards wide
	[Property, Group( "Lane" )] public Color LaneColorA { get; set; } = new Color( 0.80f, 0.62f, 0.38f, 1f );
	[Property, Group( "Lane" )] public Color LaneColorB { get; set; } = new Color( 0.74f, 0.56f, 0.33f, 1f );
	[Property, Group( "Lane" )] public Color FoulLineColor { get; set; } = new Color( 0.85f, 0.10f, 0.10f, 1f );
	[Property, Group( "Lane" )] public Color ArrowColor { get; set; } = new Color( 0.20f, 0.13f, 0.07f, 1f );
	/// <summary>Restitution of the lane structures (bed, gutters, pit, back wall). Cranked up so struck
	/// pins ricochet off the deck and walls for chaotic pin action. NOTE: this is on the surface the
	/// ball rolls too, so very high values can make the ball itself bounce — intentional overshoot.</summary>
	[Property, Group( "Lane" )] public float LaneElasticity { get; set; } = 0.9f;

	/// <summary>Distance (real inches) the throw line sits in FRONT of the head pin — the gate
	/// where modern alleys capture ball speed and, here, where control is handed to physics so you
	/// can watch the hit. 60" = 5 ft, in the 3–7 ft range asked for.</summary>
	[Property, Group( "Throw Line" )] public float ThrowLineDistanceInches { get; set; } = 60f;
	[Property, Group( "Throw Line" )] public Color ThrowLineColor { get; set; } = new Color( 0.10f, 0.85f, 0.95f, 1f );

	[Property, Group( "Approach" )] public float ApproachLengthInches { get; set; } = 180f; // 15 ft run-up
	[Property, Group( "Approach" )] public float PlatformHeightInches { get; set; } = 10f;  // play surface above the floor
	[Property, Group( "Approach" )] public float RampSlopeDegrees { get; set; } = 8f;        // shallow enough to roll up
	[Property, Group( "Approach" )] public Color ApproachColor { get; set; } = new Color( 0.66f, 0.50f, 0.30f, 1f );

	[Property, Group( "Gutters" )] public float GutterWidthInches { get; set; } = 9.25f;
	[Property, Group( "Gutters" )] public float GutterDepthInches { get; set; } = 5f;   // recessed below the lane surface
	[Property, Group( "Gutters" )] public Color GutterColor { get; set; } = new Color( 0.10f, 0.10f, 0.12f, 1f );

	[Property, Group( "Barriers" )] public float BarrierWidthInches { get; set; } = 6f;
	[Property, Group( "Barriers" )] public float BarrierHeightInches { get; set; } = 8f; // a low curb above the surface, not a wall
	[Property, Group( "Barriers" )] public float BufferWidthInches { get; set; } = 24f;  // flat margin for future decoration
	[Property, Group( "Barriers" )] public Color BarrierColor { get; set; } = new Color( 0.35f, 0.37f, 0.42f, 1f );
	[Property, Group( "Barriers" )] public Color BufferColor { get; set; } = new Color( 0.28f, 0.24f, 0.20f, 1f );

	[Property, Group( "Pit" )] public float PinDeckTailInches { get; set; } = 6f;   // lane surface past the back row
	[Property, Group( "Pit" )] public float PitLengthInches { get; set; } = 36f;
	[Property, Group( "Pit" )] public float PitDepthInches { get; set; } = 10f;
	[Property, Group( "Pit" )] public float BackWallHeightInches { get; set; } = 40f;
	[Property, Group( "Pit" )] public Color PitColor { get; set; } = new Color( 0.08f, 0.08f, 0.09f, 1f );

	[Property, Group( "Arrows" )] public float ArrowCenterDistanceInches { get; set; } = 180f; // centre (apex) arrow ~15 ft out
	[Property, Group( "Arrows" )] public float ArrowStepInches { get; set; } = 12f;            // each board-pair steps back
	[Property, Group( "Arrows" )] public float ArrowLengthInches { get; set; } = 6f;
	[Property, Group( "Arrows" )] public float ArrowWidthInches { get; set; } = 4f;

	[Property, Group( "Pins" )] public float PinSpacingInches { get; set; } = 12f;   // pin centres
	[Property, Group( "Pins" )] public float PinWidthInches { get; set; } = 4.766f; // belly (widest) diameter
	[Property, Group( "Pins" )] public float PinHeightInches { get; set; } = 15f;
	/// <summary>Mass in kg. A regulation bowling pin is ~1.6 kg (3 lb 8 oz).</summary>
	[Property, Group( "Pins" )] public float PinMass { get; set; } = 1.6f;
	/// <summary>Pin-to-lane kinetic friction. Real pins have a smooth plastic coating
	/// engineered to slide; lane CoF is ~0.18, so ~0.1–0.18 is realistic.</summary>
	[Property, Group( "Pins" )] public float PinFriction { get; set; } = 0.15f;
	/// <summary>Restitution (bounciness) of the pins. The engine default (~0.25) is dead, so pins get
	/// shoved instead of launching and cartwheeling. Real coated maple is springy (~0.5–0.7); this
	/// drives both pin-ball and pin-pin liveliness. Tune by feel.</summary>
	[Property, Group( "Pins" )] public float PinElasticity { get; set; } = 0.95f;
	[Property, Group( "Pins" )] public Color PinColor { get; set; } = new Color( 0.95f, 0.95f, 0.95f, 1f );
	/// <summary>Multiplier on the pins' SCALE-CORRECT gravity. The lane is ~10.7× bigger than world
	/// gravity expects, so 1 here = a real 1g AT THIS SCALE — real-bowling fall speed and friction.
	/// The data forced this: at world-normal gravity even a 0.9 mph ball clears 7 pins, because a
	/// kicked pin slides the whole deck unchecked (friction ∝ m·g too weak) and floats (slow falls)
	/// into its neighbours. Knock-down FORCE is gravity-independent (mass+restitution); gravity only
	/// governs fall speed + slide distance — which is what stops the unchecked chain reaction. Pins
	/// only; world gravity is untouched, so players/other modes are unaffected.</summary>
	[Property, Group( "Pins" )] public float PinGravityScale { get; set; } = 1f;

	/// <summary>Gap on the floor between the ball spawn and the foot of the ramp (units).</summary>
	[Property, Group( "Layout" )] public float SpawnGapUnits { get; set; } = 150f;

	private bool _built;
	private Model _pinModel;   // shared by all ten pins; built once at the lane's scale
	private Model _arrowModel; // shared by the seven targeting arrows
	private float _pinGravityScale = 1f; // resolved per-pin GravityScale (scale-correct × feel knob)

	/// <summary>A pin and the spot it was racked on, for the director's "is it still standing?"
	/// scoring and for re-racking after a throw.</summary>
	public readonly record struct PinPlacement( int Number, GameObject Object, Vector3 Home );

	private readonly List<PinPlacement> _pins = new();
	/// <summary>The ten pins with their home spots, exposed for the round director.</summary>
	public IReadOnlyList<PinPlacement> Pins => _pins;

	/// <summary>Units-per-inch the lane was built at — lets the director express its thresholds in
	/// real inches and scale them the same way every dimension here is scaled.</summary>
	public float UnitsPerInch { get; private set; }

	/// <summary>World X of the throw line. The director arms watch-mode when the ball crosses it.</summary>
	public float ThrowLineWorldX { get; private set; }

	/// <summary>Centre of the run-up area on the platform TOP surface (z = play surface), where the
	/// ball is spawned/re-racked. Add the ball's radius to sit it on the surface.</summary>
	public Vector3 RunUpWorldPosition { get; private set; }

	/// <summary>Centre of the pin triangle on the platform top — what the camera frames.</summary>
	public Vector3 PinDeckWorldCenter { get; private set; }

	/// <summary>World X just past the back row of pins — used to sample the ball's speed once it has
	/// cleared the whole rack (how much momentum it lost to the pins).</summary>
	public float PinDeckEndWorldX { get; private set; }

	protected override void OnEnabled()
	{
		if ( _built ) return;
		_built = true;

		// Treat the actual ball as 8.5" to get a units-per-inch scale for everything else.
		var ball = Scene.GetAllComponents<BallController>().FirstOrDefault();
		var ballDiameterUnits = ball.IsValid() ? ball.Radius * 2f : 200f;
		var u = ballDiameterUnits / BallDiameterInches; // units per inch
		UnitsPerInch = u;

		// Resolve the pins' gravity: scale-correct 1g at this lane size (386 in/s² → units, ÷ world g)
		// times the feel knob. Stops a kicked pin from sliding/floating across the whole deck.
		var worldG = Scene.PhysicsWorld.Gravity.Length;
		_pinGravityScale = worldG > 1f ? (386f * u) / worldG * PinGravityScale : PinGravityScale;

		// --- Vertical: platform top is the play surface; gutters recess below, barriers rise above.
		var H = PlatformHeightInches * u;
		var gutterD = GutterDepthInches * u;
		var barrierH = BarrierHeightInches * u;
		var pitD = PitDepthInches * u;
		var backWallH = BackWallHeightInches * u;

		// --- Lateral half-widths (units), out from the lane centre line.
		var laneHalf = LaneWidthInches * u * 0.5f;
		var gutterW = GutterWidthInches * u;
		var barrierW = BarrierWidthInches * u;
		var bufferW = BufferWidthInches * u;
		var gutterOuter = laneHalf + gutterW;               // lane+gutter span (gutter outer edge)
		var barrierOuter = gutterOuter + barrierW;
		var platformHalf = barrierOuter + bufferW;          // platform edge

		// --- Longitudinal landmarks (units), foul line = x datum, laid out so the ramp foot
		// rests on the floor just ahead of the ball spawn at the origin.
		var approachLen = ApproachLengthInches * u;
		var slope = MathX.DegreeToRadian( RampSlopeDegrees );
		var rampRun = H / MathF.Tan( slope );
		var foulX = SpawnGapUnits + approachLen + rampRun;  // ⇒ ramp foot lands at x = SpawnGapUnits
		var approachBackX = foulX - approachLen;            // top of ramp meets the approach
		var rampBottomX = approachBackX - rampRun;

		var laneLen = LaneLengthInches * u;
		var pinSpace = PinSpacingInches * u;
		var rowDepth = pinSpace * 0.8660254f;               // 12" centres, rows step back by 12·cos(30°)
		var headPinX = foulX + laneLen;
		var backRowX = headPinX + rowDepth * 3f;
		var pinDeckEndX = backRowX + PinDeckTailInches * u;  // lane surface stops here

		// Where the ball is racked for a throw: a quarter into the approach from its back edge,
		// centred across the lane, on the platform top. And the pin-triangle centre, for the camera.
		RunUpWorldPosition = new Vector3( approachBackX + approachLen * 0.25f, 0f, H );
		PinDeckWorldCenter = new Vector3( (headPinX + backRowX) * 0.5f, 0f, H );
		PinDeckEndWorldX = pinDeckEndX;
		var pitEndX = pinDeckEndX + PitLengthInches * u;

		// ---- Ramp: one sloped slab whose TOP surface runs from the floor (z = 0) up to the
		// platform top (z = H), so you roll straight on with no lip to bump over. The slab body is
		// pushed down along its own normal by half its thickness, so it dips below the floor (they
		// intersect — that's fine) and only the flush top is in play.
		var rampA = MathX.DegreeToRadian( RampSlopeDegrees );
		var rampThk = 6f * u;
		var rampLen = MathF.Sqrt( rampRun * rampRun + H * H );
		var rampMidX = (rampBottomX + approachBackX) * 0.5f;
		MakeBox( "Ramp",
			new Vector3( rampMidX + MathF.Sin( rampA ) * rampThk * 0.5f, 0f, H * 0.5f - MathF.Cos( rampA ) * rampThk * 0.5f ),
			new Vector3( rampLen, platformHalf * 2f, rampThk ), ApproachColor,
			rot: Rotation.FromPitch( -RampSlopeDegrees ) );

		// ---- Lane bed: ONE continuous collider for the entire rolling path, approach through pin
		// deck, top at z = H. A single box means there's no cross-lane seam at the foul line for the
		// ball to catch on and pop into the air — the surface it travels is unbroken, so it also
		// coasts and brakes cleanly instead of skipping.
		var laneCx = (approachBackX + pinDeckEndX) * 0.5f;
		var laneLenX = pinDeckEndX - approachBackX;
		MakeBox( "LaneBed", new Vector3( laneCx, 0f, H * 0.5f ),
			new Vector3( laneLenX, laneHalf * 2f, H ), LaneColorA );

		// ---- Approach shoulders: fill the run-up out to full platform width either side of the bed
		// (the gutters haven't started yet here). Flush top at z = H.
		var shoulderW = platformHalf - laneHalf;
		var shoulderCx = (approachBackX + foulX) * 0.5f;
		var shoulderLenX = foulX - approachBackX;
		var shoulderCy = (laneHalf + platformHalf) * 0.5f;
		MakeBox( "ApproachShoulderLeft", new Vector3( shoulderCx, -shoulderCy, H * 0.5f ),
			new Vector3( shoulderLenX, shoulderW, H ), ApproachColor );
		MakeBox( "ApproachShoulderRight", new Vector3( shoulderCx, shoulderCy, H * 0.5f ),
			new Vector3( shoulderLenX, shoulderW, H ), ApproachColor );

		// ---- Gutters: recessed troughs from the foul line down; the bed's side is the inner wall,
		// the barrier the outer.
		var laneRegionCx = (foulX + pinDeckEndX) * 0.5f;
		var laneRegionLenX = pinDeckEndX - foulX;
		var gutterCy = laneHalf + gutterW * 0.5f;
		var gutterTop = H - gutterD;
		MakeBox( "GutterLeft", new Vector3( laneRegionCx, -gutterCy, gutterTop * 0.5f ),
			new Vector3( laneRegionLenX, gutterW, gutterTop ), GutterColor );
		MakeBox( "GutterRight", new Vector3( laneRegionCx, gutterCy, gutterTop * 0.5f ),
			new Vector3( laneRegionLenX, gutterW, gutterTop ), GutterColor );

		// ---- Barrier curbs + buffer margins, foul line through the pit. Barriers are low rails just
		// outboard of each gutter; buffers are flat margin for future decoration. Both start at the
		// foul line so they never overlap the approach shoulders (overlap would z-fight/flicker).
		var sideCx = (foulX + pitEndX) * 0.5f;
		var sideLenX = pitEndX - foulX;
		var barrierCy = gutterOuter + barrierW * 0.5f;
		MakeBox( "BarrierLeft", new Vector3( sideCx, -barrierCy, (H + barrierH) * 0.5f ),
			new Vector3( sideLenX, barrierW, H + barrierH ), BarrierColor );
		MakeBox( "BarrierRight", new Vector3( sideCx, barrierCy, (H + barrierH) * 0.5f ),
			new Vector3( sideLenX, barrierW, H + barrierH ), BarrierColor );

		var bufferCy = barrierOuter + bufferW * 0.5f;
		MakeBox( "BufferLeft", new Vector3( sideCx, -bufferCy, H * 0.5f ),
			new Vector3( sideLenX, bufferW, H ), BufferColor );
		MakeBox( "BufferRight", new Vector3( sideCx, bufferCy, H * 0.5f ),
			new Vector3( sideLenX, bufferW, H ), BufferColor );

		// ---- Pit: a recessed floor behind the pins so there's no instant falloff, then a back wall.
		var pitTop = H - pitD;
		MakeBox( "Pit", new Vector3( (pinDeckEndX + pitEndX) * 0.5f, 0f, pitTop * 0.5f ),
			new Vector3( pitEndX - pinDeckEndX, gutterOuter * 2f, pitTop ), PitColor );
		var backThk = 12f * u;
		MakeBox( "BackWall", new Vector3( pitEndX + backThk * 0.5f, 0f, (H + backWallH) * 0.5f ),
			new Vector3( backThk, platformHalf * 2f, H + backWallH ), BarrierColor );

		// ---- Boards: 39 alternating-tint planks across the lane, straddling z = H so they read as
		// the surface itself (not floating above it) with the seams showing through. Visual-only —
		// no collider — so the ball never catches them. Bowlers aim by board number, so they're
		// functional, not just decoration.
		var boardW = (laneHalf * 2f) / BoardCount;
		for ( var b = 0; b < BoardCount; b++ )
		{
			var by = laneHalf - (b + 0.5f) * boardW;
			MakeDecal( $"Board{b + 1}", new Vector3( laneRegionCx, by, H ),
				new Vector3( laneRegionLenX, boardW * 0.92f, 4f ),
				(b % 2 == 0) ? LaneColorA : LaneColorB );
		}

		// ---- Foul line: a bright line across the lane and both gutters at the lane's start, flush
		// on the surface and a hair thicker than the boards so it reads over them.
		MakeDecal( "FoulLine", new Vector3( foulX, 0f, H ),
			new Vector3( 2f * u, gutterOuter * 2f, 6f ), FoulLineColor );

		// ---- Throw line: the speed-capture gate a short way in front of the pins. Same flush
		// cross-lane stripe as the foul line but its own colour; the director watches for the ball
		// to cross ThrowLineWorldX and hands control to physics for the hit.
		ThrowLineWorldX = headPinX - ThrowLineDistanceInches * u;
		MakeDecal( "ThrowLine", new Vector3( ThrowLineWorldX, 0f, H ),
			new Vector3( 2f * u, gutterOuter * 2f, 6.5f ), ThrowLineColor );

		// ---- Arrows: the seven targeting arrows in their dovetail chevron — centre (apex) arrow
		// deepest, outers stepping BACK toward the foul line, so the whole formation points
		// down-lane at the pins (matching each arrow's own heading), as on a real lane. Flat
		// visual-only decals sitting just above the boards.
		_arrowModel = BuildArrowModel( u );
		int[] arrowBoards = { 5, 10, 15, 20, 25, 30, 35 };
		var arrowZ = H + 4f;
		foreach ( var board in arrowBoards )
		{
			var ay = laneHalf - (board - 0.5f) * boardW;
			var steps = MathF.Abs( board - 20 ) / 5f; // 0 at centre board, up to 3 at the outers
			var ax = foulX + (ArrowCenterDistanceInches - steps * ArrowStepInches) * u;
			MakeArrow( $"Arrow{board}", new Vector3( ax, ay, arrowZ ) );
		}

		// ---- Pins: regulation triangle on the platform top — head pin nearest the bowler, rows of
		// 1/2/3/4 stepping away, each centred across the lane.
		_pinModel = BuildPinModel( u );
		var n = 1;
		for ( var r = 0; r < 4; r++ )
		{
			var px = headPinX + r * rowDepth;
			for ( var i = 0; i <= r; i++ )
			{
				var py = (i - r * 0.5f) * pinSpace;
				var home = new Vector3( px, py, H ); // base sits on the platform top
				var pin = MakePin( $"Pin{n}", home );
				_pins.Add( new PinPlacement( n, pin, home ) );
				n++;
			}
		}
	}

	// Static box with collider on the (uniformly-scaled) root and the visual on a non-uniformly
	// scaled child (s&box freezes physics if a BoxCollider rides a non-uniform transform scale).
	// A rotation, if given, is applied to the root so the collider tilts with the visual (ramp).
	private void MakeBox( string name, Vector3 worldPos, Vector3 size, Color tint, Rotation? rot = null )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;
		if ( rot.HasValue ) go.WorldRotation = rot.Value;

		var col = go.Components.GetOrCreate<BoxCollider>();
		col.Scale = size;
		col.Elasticity = LaneElasticity; // bouncy deck/walls so pins ricochet (cranked for chaos)

		var visual = new GameObject( true, name + "Visual" ) { Parent = go };
		var mr = visual.Components.GetOrCreate<ModelRenderer>();
		mr.Model = Model.Load( "models/dev/box.vmdl" );
		mr.Tint = tint;

		var b = mr.Model.Bounds.Size;
		visual.LocalScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );
	}

	// Visual-only thin slab (no collider) laid on a surface — boards, foul line, etc.
	private void MakeDecal( string name, Vector3 worldPos, Vector3 size, Color tint )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;

		var mr = go.Components.GetOrCreate<ModelRenderer>();
		mr.Model = Model.Load( "models/dev/box.vmdl" );
		mr.Tint = tint;

		var b = mr.Model.Bounds.Size;
		go.WorldScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );
	}

	// One of the seven targeting arrows: a flat triangle decal pointing back at the bowler.
	private void MakeArrow( string name, Vector3 worldPos )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;
		var mr = go.Components.GetOrCreate<ModelRenderer>();
		mr.Model = _arrowModel;
		mr.Tint = ArrowColor;
	}

	// A flat isosceles triangle in the XY plane with its apex toward +x (down-lane, toward the
	// pins, as on a real lane), faces up. Both windings are emitted so it's never culled.
	private Model BuildArrowModel( float u )
	{
		var len = ArrowLengthInches * u;
		var wid = ArrowWidthInches * u;

		var vb = new VertexBuffer();
		vb.Init( true );
		var apex = new Vector3( len, 0f, 0f );
		var baseL = new Vector3( 0f, -wid * 0.5f, 0f );
		var baseR = new Vector3( 0f, wid * 0.5f, 0f );
		vb.Add( new Vertex( apex, Vector3.Up, Vector3.Zero, new Vector4( 0f, 0.5f, 0f, 0f ) ) );
		vb.Add( new Vertex( baseL, Vector3.Up, Vector3.Zero, new Vector4( 1f, 0f, 0f, 0f ) ) );
		vb.Add( new Vertex( baseR, Vector3.Up, Vector3.Zero, new Vector4( 1f, 1f, 0f, 0f ) ) );
		vb.AddRawIndex( 0 ); vb.AddRawIndex( 1 ); vb.AddRawIndex( 2 ); // up-facing
		vb.AddRawIndex( 0 ); vb.AddRawIndex( 2 ); vb.AddRawIndex( 1 ); // and back so it's never culled

		var mesh = new Mesh( Material.Load( "materials/default/white.vmat" ) );
		mesh.CreateBuffers( vb );
		var mb = new ModelBuilder();
		mb.AddMesh( mesh );
		return mb.Create();
	}

	/// <summary>
	/// Regulation pin silhouette as (height fraction up from the base, diameter as a fraction
	/// of the belly diameter). Revolving this profile gives the classic bowling-pin shape, and
	/// because the solid is built straight from this curve the belly's volume sits low and the
	/// mass distribution is realistic without any manual centre-of-mass tweak.
	/// </summary>
	private static readonly (float t, float dia)[] PinSilhouette =
	{
		(0.00f, 0.432f), // base
		(0.05f, 0.472f),
		(0.10f, 0.617f),
		(0.20f, 0.881f),
		(0.30f, 1.000f), // belly — widest
		(0.40f, 0.923f),
		(0.50f, 0.713f),
		(0.60f, 0.483f),
		(0.667f, 0.377f), // neck — narrowest
		(0.75f, 0.483f),
		(0.85f, 0.611f), // head — flared
		(0.93f, 0.483f),
		(1.00f, 0.252f), // domed top (kept >0 so the cap isn't degenerate)
	};

	// A pin instance: Rigidbody + ModelRenderer + ModelCollider, all pointing at the one shared
	// lathed pin Model. The ModelCollider's convex hulls feed the Rigidbody's mass build, so the
	// inertia tensor and CoM come from the true pin solid.
	private GameObject MakePin( string name, Vector3 worldPos )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;

		// Rigidbody first; MassOverride after the collider (adding the shape rebuilds mass from
		// volume via BuildMass, which would otherwise clobber the override).
		var rb = go.Components.GetOrCreate<Rigidbody>();

		var mr = go.Components.Create<ModelRenderer>();
		mr.Model = _pinModel;
		mr.Tint = PinColor;

		var col = go.Components.Create<ModelCollider>();
		col.Model = _pinModel;
		col.Friction = PinFriction; // smooth plastic-coated pin slides & scatters
		col.Elasticity = PinElasticity; // springy maple — pins launch & cartwheel off ball and each other

		rb.MassOverride = PinMass;
		rb.EnhancedCcd = true; // struck at bowling speed — don't let the ball tunnel through
		rb.GravityScale = _pinGravityScale; // scale-correct 1g at lane size × feel knob (pins only)

		return go;
	}

	// Build the shared pin Model by revolving PinSilhouette around the vertical axis: a smooth
	// render mesh, plus one convex-hull frustum per profile slab for simulatable collision (a
	// single hull can't capture the concave neck, but a stack of convex slabs can).
	private Model BuildPinModel( float u )
	{
		var height = PinHeightInches * u;
		var bellyRadius = PinWidthInches * u * 0.5f; // belly diameter is the profile's "1.0"
		const int radial = 24;                       // segments around the circumference

		var rings = PinSilhouette.Length;
		var z = new float[rings];
		var r = new float[rings];
		for ( var i = 0; i < rings; i++ )
		{
			z[i] = PinSilhouette[i].t * height;
			r[i] = PinSilhouette[i].dia * bellyRadius;
		}

		var vb = new VertexBuffer();
		vb.Init( true );

		// Side surface: one vertex ring per profile sample, normals from the profile slope.
		var ringPos = new List<Vector3>[rings];
		for ( var i = 0; i < rings; i++ )
		{
			// Outward normal in the (radius, z) plane = profile tangent rotated -90°.
			var dz = (i < rings - 1 ? z[i + 1] : z[i]) - (i > 0 ? z[i - 1] : z[i]);
			var dr = (i < rings - 1 ? r[i + 1] : r[i]) - (i > 0 ? r[i - 1] : r[i]);
			var nPlane = new Vector2( dz, -dr ).Normal; // (radial, axial)

			ringPos[i] = new List<Vector3>( radial );
			for ( var j = 0; j < radial; j++ )
			{
				var a = j / (float)radial * MathF.PI * 2f;
				var c = MathF.Cos( a );
				var s = MathF.Sin( a );
				var pos = new Vector3( r[i] * c, r[i] * s, z[i] );
				var normal = new Vector3( nPlane.x * c, nPlane.x * s, nPlane.y ).Normal;
				vb.Add( new Vertex( pos, normal, Vector3.Zero, new Vector4( j / (float)radial, PinSilhouette[i].t, 0f, 0f ) ) );
				ringPos[i].Add( pos );
			}
		}

		// Side triangles between consecutive rings. (If pins ever render inside-out, swap the
		// winding here — the lighting uses the explicit normals above, so only culling cares.)
		for ( var i = 0; i < rings - 1; i++ )
		{
			for ( var j = 0; j < radial; j++ )
			{
				var jn = (j + 1) % radial;
				var a = i * radial + j;
				var b = i * radial + jn;
				var cc = (i + 1) * radial + j;
				var d = (i + 1) * radial + jn;
				vb.AddRawIndex( a ); vb.AddRawIndex( cc ); vb.AddRawIndex( b );
				vb.AddRawIndex( b ); vb.AddRawIndex( cc ); vb.AddRawIndex( d );
			}
		}

		// Bottom and top caps (fans to a centre vertex) so the pin reads as a closed solid.
		// (VertexBuffer's vertex list is internal, so track the running count ourselves.)
		var vertCount = rings * radial;
		vertCount += AddCap( vb, ringPos[0], z[0], radial, vertCount, faceDown: true );
		AddCap( vb, ringPos[rings - 1], z[rings - 1], radial, vertCount, faceDown: false );

		var mesh = new Mesh( Material.Load( "materials/default/white.vmat" ) );
		mesh.CreateBuffers( vb );

		// Collision: a convex hull per slab (both rings of positions). Convex per slab handles
		// the concave neck a single hull would fill in; the stack closes the whole solid.
		var builder = new ModelBuilder();
		builder.AddMesh( mesh );
		for ( var i = 0; i < rings - 1; i++ )
		{
			var hull = new List<Vector3>( radial * 2 );
			hull.AddRange( ringPos[i] );
			hull.AddRange( ringPos[i + 1] );
			builder.AddCollisionHull( hull );
		}
		builder.WithMass( PinMass );
		return builder.Create();
	}

	// Adds a fan cap over <paramref name="ring"/> centred on the axis, with its first vertex at
	// absolute index <paramref name="firstIndex"/>; returns how many vertices it added.
	private static int AddCap( VertexBuffer vb, List<Vector3> ring, float zc, int radial, int firstIndex, bool faceDown )
	{
		var normal = faceDown ? Vector3.Down : Vector3.Up;
		var centerIdx = firstIndex;
		vb.Add( new Vertex( new Vector3( 0f, 0f, zc ), normal, Vector3.Zero, new Vector4( 0.5f, 0.5f, 0f, 0f ) ) );

		var ringStart = firstIndex + 1;
		for ( var j = 0; j < radial; j++ )
			vb.Add( new Vertex( ring[j], normal, Vector3.Zero, new Vector4( j / (float)radial, 0f, 0f, 0f ) ) );

		for ( var j = 0; j < radial; j++ )
		{
			var a = ringStart + j;
			var b = ringStart + (j + 1) % radial;
			// Wind so the visible face points the way the normal does.
			if ( faceDown ) { vb.AddRawIndex( centerIdx ); vb.AddRawIndex( a ); vb.AddRawIndex( b ); }
			else { vb.AddRawIndex( centerIdx ); vb.AddRawIndex( b ); vb.AddRawIndex( a ); }
		}

		return 1 + radial;
	}
}
