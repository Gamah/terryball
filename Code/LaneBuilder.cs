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
	/// <summary>Restitution of the lane structures (bed, gutters, pit, back wall). Modest — enough that
	/// struck pins ricochet off the deck/walls a little, but not so high the rolling ball hops on the
	/// bed. (Was cranked to 0.9 as an overshoot; walked back with the rest of the restitution pass.)</summary>
	[Property, Group( "Lane" )] public float LaneElasticity { get; set; } = 0.3f;

	/// <summary>Restitution of the PIT FLOOR and BACK WALL only — the catch zone behind the pins.
	/// Near-dead so the ball dumps its energy instead of rebounding off a bouncy wall back into the
	/// rack. Real pits hang a cushion/curtain for exactly this; here it's a dead, grippy surface.</summary>
	[Property, Group( "Pit" )] public float BackstopElasticity { get; set; } = 0.02f;
	/// <summary>Friction of the pit floor and back wall. Deliberately LOW: a heavily-revved ball jammed
	/// into the floor↔wall corner of a high-friction pit grips and launches itself back up the lane.
	/// A real pit cushion kills the ball by being dead (low elasticity), not grippy — so we keep this
	/// slick and let the near-zero restitution do the absorbing.</summary>
	[Property, Group( "Pit" )] public float BackstopFriction { get; set; } = 0.2f;

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
	[Property, Group( "Barriers" )] public float BarrierHeightInches { get; set; } = 3f; // a low lip flush along the gutter, not a wall
	[Property, Group( "Barriers" )] public float BufferWidthInches { get; set; } = 24f;  // flat margin for future decoration
	[Property, Group( "Barriers" )] public Color BarrierColor { get; set; } = new Color( 0.35f, 0.37f, 0.42f, 1f );
	[Property, Group( "Barriers" )] public Color BufferColor { get; set; } = new Color( 0.28f, 0.24f, 0.20f, 1f );

	[Property, Group( "Pit" )] public float PinDeckTailInches { get; set; } = 6f;   // lane surface past the back row
	[Property, Group( "Pit" )] public float PitLengthInches { get; set; } = 36f;
	[Property, Group( "Pit" )] public float PitDepthInches { get; set; } = 24f;
	/// <summary>How far the backboard deck's flat top rises ABOVE the lane surface (real inches × lane
	/// scale). The deck behind the pit doubles as the catch wall AND the raised platform the settee sits
	/// on — short, so the spectators watching the ball roll toward them aren't perched up a tall wall.
	/// Just above pin height (15") so they see over the rack down the lane.</summary>
	[Property, Group( "Pit" )] public float BackboardHeightInches { get; set; } = 18f;
	[Property, Group( "Pit" )] public Color PitColor { get; set; } = new Color( 0.08f, 0.08f, 0.09f, 1f );

	[Property, Group( "Arrows" )] public float ArrowCenterDistanceInches { get; set; } = 180f; // centre (apex) arrow ~15 ft out
	[Property, Group( "Arrows" )] public float ArrowStepInches { get; set; } = 12f;            // each board-pair steps back
	[Property, Group( "Arrows" )] public float ArrowLengthInches { get; set; } = 6f;
	[Property, Group( "Arrows" )] public float ArrowWidthInches { get; set; } = 4f;

	[Property, Group( "Pins" )] public float PinSpacingInches { get; set; } = 12f;   // pin centres
	[Property, Group( "Pins" )] public float PinWidthInches { get; set; } = 4.766f; // belly (widest) diameter
	[Property, Group( "Pins" )] public float PinHeightInches { get; set; } = 15f;
	/// <summary>Mass in kg. A regulation bowling pin is ~1.6 kg (3 lb 8 oz) — but at that mass the
	/// cascade is realistically efficient and a flush hit wrecks the rack (correct, but too-easy
	/// strikes by feel). This is now a DIFFICULTY DIAL, deliberately detached from reality: heavier
	/// pins fly slower off the ball and a cascade-struck pin can't always topple its neighbour, so
	/// the back corners survive more often → fewer strikes, more spares. Tune by feel.</summary>
	[Property, Group( "Pins" )] public float PinMass { get; set; } = 2.5f;
	/// <summary>Pin-to-lane kinetic friction. Real pins have a smooth plastic coating
	/// engineered to slide; lane CoF is ~0.18, so ~0.1–0.18 is realistic.</summary>
	[Property, Group( "Pins" )] public float PinFriction { get; set; } = 0.15f;
	/// <summary>Restitution (bounciness) of the pins. By the collision algebra, HIGH restitution both
	/// flings the pin away faster AND makes the ball lose more speed to it — both wanted. Low values
	/// make ball+pin move together (ball plows, pin doesn't fly). Keep high; the steamroll was a
	/// contact/CCD bug, not this. Drives pin-ball and pin-pin liveliness.</summary>
	[Property, Group( "Pins" )] public float PinElasticity { get; set; } = 0.3f;
	[Property, Group( "Pins" )] public Color PinColor { get; set; } = new Color( 1f, 1f, 1f, 1f );
	/// <summary>The two trim stripes ringing the pin's neck.</summary>
	[Property, Group( "Pins" )] public Color PinStripeColor { get; set; } = new Color( 0.78f, 0.07f, 0.09f, 1f );
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

	// ---- Settee: the bowlers' seating area on the floor behind the lane, at the foot of the ramp.
	// (Real-alley term: the "settee".) A defined carpeted riser with bench seats, plus the overhead
	// scoring monitor on posts. Walking-Citizen bowlers (Terry) now stand and sit here, so THEY are the
	// ruler — everything is sized in real inches at CITIZEN scale (~1 u/in, derived from the Citizen's
	// height below), NOT the lane's much larger units-per-inch. (The lane treats the 200u ball as 8.5",
	// so the lane is ~23× a person; sizing the settee off the ball ballooned it far past the bowlers —
	// hence the earlier "balls" units. Tune by feel.)
	[Property, Group( "Settee" )] public float BowlerHeightUnits { get; set; } = 72f;    // the s&box Citizen is ≈ 72u tall
	[Property, Group( "Settee" )] public float BowlerHeightInches { get; set; } = 72f;   // treat Terry as a 6 ft human → ~1 u/in
	[Property, Group( "Settee" )] public float SetteeDepthInches { get; set; } = 220f;   // how far back the seating zone runs from the ramp foot
	[Property, Group( "Settee" )] public float SetteeWidthInches { get; set; } = 360f;   // min total width; grows to seat the boards
	[Property, Group( "Settee" )] public float SetteeRiserInches { get; set; } = 4f;     // low step up onto the carpeted zone
	[Property, Group( "Settee" )] public float BenchSeatHeightInches { get; set; } = 18f;
	[Property, Group( "Settee" )] public float BenchSeatDepthInches { get; set; } = 22f;
	[Property, Group( "Settee" )] public float BenchBackHeightInches { get; set; } = 18f; // backrest above the seat
	[Property, Group( "Settee" )] public Color SetteeCarpetColor { get; set; } = new Color( 0.16f, 0.10f, 0.22f, 1f );
	[Property, Group( "Settee" )] public Color BenchFrameColor { get; set; } = new Color( 0.18f, 0.19f, 0.22f, 1f );
	[Property, Group( "Settee" )] public Color BenchCushionColor { get; set; } = new Color( 0.55f, 0.13f, 0.16f, 1f );

	// ---- Monitor: the overhead score + camera screens above the settee. WorldPanels in physical
	// bezels on posts, facing back at the seats (toward -x). Citizen-scale too (real inches), so they
	// read at head height for the people on the settee instead of dominating the back wall.
	[Property, Group( "Monitor" )] public float MonitorWidthInches { get; set; } = 240f;        // per-board width (2× the first pass); height follows 16:9
	[Property, Group( "Monitor" )] public float MonitorCenterHeightInches { get; set; } = 120f; // board centre above the settee floor (clears the taller boards)
	[Property, Group( "Monitor" )] public Color MonitorBezelColor { get; set; } = new Color( 0.05f, 0.06f, 0.08f, 1f );
	[Property, Group( "Monitor" )] public Color MonitorPostColor { get; set; } = new Color( 0.22f, 0.23f, 0.26f, 1f );

	private bool _built;
	private Model _pinModel;   // shared by all ten pins; built once at the lane's scale
	private PinImpactAudio _pinAudio; // shared procedural-crash bank; pins route collisions here
	private Model _arrowModel; // shared by the seven targeting arrows
	private readonly List<ModelRenderer> _arrowRenderers = new(); // re-skinned in OnStart (see note there)
	private Material _pinMat;       // glossy white pin body
	private Material _pinStripeMat; // red neck stripes (same shader, red tint)
	private float _pinGravityScale = 1f; // resolved per-pin GravityScale (scale-correct × feel knob)

	/// <summary>A pin and the spot it was racked on, for the director's "is it still standing?"
	/// scoring and for re-racking after a throw.</summary>
	public readonly record struct PinPlacement( int Number, GameObject Object, Vector3 Home );

	// The pins are now NETWORKED objects (see Pin.cs): the host spawns them, the engine replicates them.
	// So this lane doesn't own a private list — it RESOLVES its pins live from the Pin components in its
	// own subtree, ordered by number. _pinSpots is just the rack layout the host spawns from.
	private readonly List<(int Number, Vector3 Home)> _pinSpots = new();
	// The lane's live Pin components, registered by each Pin on start. A registry (not a scene scan) so
	// SWEPT pins — disabled GameObjects, which a scene-wide component query skips — stay visible to the
	// director and get re-racked. Every client maintains its own as the networked pins replicate in.
	private readonly List<Pin> _pinComponents = new();

	/// <summary>True once the host has spawned this lane's ten networked pins.</summary>
	public bool PinsSpawned { get; private set; }

	/// <summary>A <see cref="Pin"/> registers itself here on start (and unregisters on destroy) so the
	/// director can address it by number even while it's swept off the deck (disabled).</summary>
	public void RegisterPin( Pin pin )
	{
		if ( pin.IsValid() && !_pinComponents.Contains( pin ) ) _pinComponents.Add( pin );
	}
	public void UnregisterPin( Pin pin ) => _pinComponents.Remove( pin );

	/// <summary>The ten pins with their home spots, ordered by number — identical on every client.
	/// Returns a fresh list (includes swept/disabled pins) so a caller can safely re-query mid-iteration.</summary>
	public IReadOnlyList<PinPlacement> Pins
	{
		get
		{
			var view = new List<PinPlacement>( _pinComponents.Count );
			foreach ( var pin in _pinComponents )
				if ( pin.IsValid() )
					view.Add( new PinPlacement( pin.Number, pin.GameObject, pin.Home ) );
			view.Sort( static ( a, b ) => a.Number.CompareTo( b.Number ) );
			return view;
		}
	}

	/// <summary>Units-per-inch the lane was built at — lets the director express its thresholds in
	/// real inches and scale them the same way every dimension here is scaled.</summary>
	public float UnitsPerInch { get; private set; }

	/// <summary>World X of the throw line. The director arms watch-mode when the ball crosses it.</summary>
	public float ThrowLineWorldX { get; private set; }
	/// <summary>World x of the foul line (lane entry). The ball crossing this is the "throw".</summary>
	public float FoulLineWorldX { get; private set; }

	/// <summary>Centre of the run-up area on the platform TOP surface (z = play surface), where the
	/// ball is spawned/re-racked. Add the ball's radius to sit it on the surface.</summary>
	public Vector3 RunUpWorldPosition { get; private set; }

	/// <summary>Centre of the pin triangle on the platform top — what the camera frames.</summary>
	public Vector3 PinDeckWorldCenter { get; private set; }

	/// <summary>World X just past the back row of pins — used to sample the ball's speed once it has
	/// cleared the whole rack (how much momentum it lost to the pins).</summary>
	public float PinDeckEndWorldX { get; private set; }

	/// <summary>Centre of the settee carpet (on its top surface), where the director spawns walking
	/// bowlers.</summary>
	public Vector3 SetteeFloorCenter { get; private set; }

	/// <summary>Half the settee's usable width (units) — the director spreads spawned bowlers across
	/// it so they don't stack on one spot.</summary>
	public float SetteeHalfWidthUnits { get; private set; }

	/// <summary>Half the settee's usable DEPTH (units, along the lane axis) — paired with the half-width
	/// for the on-settee footprint test (<see cref="IsOnSettee"/>).</summary>
	public float SetteeHalfDepthUnits { get; private set; }

	/// <summary>Is <paramref name="worldPos"/> standing on the settee carpet footprint? The crane sets a
	/// bowler back down on the exact spot it grabbed them ONLY when that spot was on the settee; a bowler
	/// grabbed from off it (e.g. up at the lane station after pressing START) is dropped at the middle
	/// instead. Tested in lane-local space so it's correct at any lane offset/rotation.</summary>
	public bool IsOnSettee( Vector3 worldPos )
	{
		if ( SetteeHalfWidthUnits <= 0f ) return false;
		var rot = Transform.World.Rotation;
		var rel = worldPos - SetteeFloorCenter;
		return MathF.Abs( rel.Dot( rot.Forward ) ) <= SetteeHalfDepthUnits
			&& MathF.Abs( rel.Dot( rot.Left ) ) <= SetteeHalfWidthUnits;
	}

	/// <summary>World rotation a walking bowler should face when stood on the settee so they look down
	/// the lane. The settee now sits behind the pins on the backboard, so they face back up-lane (-x)
	/// and watch the ball roll toward them. Read by the director/avatar on spawn + return.</summary>
	public Rotation SetteeFaceRotation { get; private set; } = Rotation.FromYaw( 180f );

	protected override void OnEnabled()
	{
		if ( _built ) return;
		_built = true;

		// Treat the actual ball as 8.5" to get a units-per-inch scale for everything else.
		var ball = Lane.Of( this )?.Ball;
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
		var backboardRise = BackboardHeightInches * u; // backboard deck top above the lane surface

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
		// These are exposed in WORLD space (consumers compare against world ball positions), so the
		// lane-local landmarks are pushed through the lane root transform — correct at any lane offset.
		RunUpWorldPosition = ToWorld( new Vector3( approachBackX + approachLen * 0.25f, 0f, H ) );
		PinDeckWorldCenter = ToWorld( new Vector3( (headPinX + backRowX) * 0.5f, 0f, H ) );
		PinDeckEndWorldX = ToWorld( new Vector3( pinDeckEndX, 0f, 0f ) ).x;
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

		// ---- Pit: a recessed catch behind the pins. The floor is a real dead SLAB whose top sits pitD
		// below the deck — but never below the building floor (z = 0), which the ball can't pass anyway.
		// (Earlier this was a box of HEIGHT H − pitD; once pitD exceeded the platform height that went
		// negative, giving an inverted collider that flung the ball off the wall. A slab + clamping the
		// floor to z ≥ 0 keeps it valid at any depth.)
		var pitThk = 12f * u;
		var pitFloorTopZ = MathF.Max( H - pitD, 0f );
		var pitBottomZ = pitFloorTopZ - pitThk; // pit floor slab + backboard both reach down to here

		// Citizen units-per-inch — the settee + the deck that hosts it are sized to the walking bowlers,
		// not the giant ball. Computed here because the backboard deck depth is the settee's footprint.
		var cu = BowlerHeightInches > 0.01f ? BowlerHeightUnits / BowlerHeightInches : 1f;

		MakeBox( "Pit", new Vector3( (pinDeckEndX + pitEndX) * 0.5f, 0f, pitFloorTopZ - pitThk * 0.5f ),
			new Vector3( pitEndX - pinDeckEndX, gutterOuter * 2f, pitThk ), PitColor,
			elasticity: BackstopElasticity, friction: BackstopFriction );

		// ---- Backboard: a SHORT, WIDE, DEEP deck behind the pit. Its lane-facing face (at pitEndX) is the
		// pit's dead catch wall — kept dead so the ball dumps its energy here instead of rebounding. Its
		// flat top (a little above the rack) is the raised platform the SETTEE sits on, so spectators
		// watch the ball roll toward them. Deep enough in x to seat the settee's footprint, full width.
		var deckTopZ = H + backboardRise;
		var deckFrontX = pitEndX;                          // lane-facing edge = the catch wall
		var deckDepth = SetteeDepthInches * cu * 1.25f;     // settee footprint + walking margin
		var deckBackX = deckFrontX + deckDepth;
		MakeBox( "Backboard", new Vector3( (deckFrontX + deckBackX) * 0.5f, 0f, (pitBottomZ + deckTopZ) * 0.5f ),
			new Vector3( deckDepth, platformHalf * 2f, deckTopZ - pitBottomZ ), BarrierColor,
			elasticity: BackstopElasticity, friction: BackstopFriction );

		// ---- Backstop readout: a LARGE, lane-width pin board floating above the backboard, facing back
		// at the bowler. Same director data as the corner overlay, blown up so the leave reads from the
		// approach. (pitEndX is the deck's bowler-facing face; deckTopZ its flat top.)
		BuildBackstopReadout( pitEndX, laneHalf, deckTopZ, ballDiameterUnits );

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
		var throwLineX = headPinX - ThrowLineDistanceInches * u;            // lane-local x
		ThrowLineWorldX = ToWorld( new Vector3( throwLineX, 0f, 0f ) ).x;   // exposed in world space
		FoulLineWorldX = ToWorld( new Vector3( foulX, 0f, 0f ) ).x;         // lane entry — drives the throw sound
		MakeDecal( "ThrowLine", new Vector3( throwLineX, 0f, H ),
			new Vector3( 2f * u, gutterOuter * 2f, 6.5f ), ThrowLineColor );

		// ---- Announcer call-out: a lane-width text banner floating over the lane, tilted back to face
		// the approach, where the call (STRIKE! / SPLIT! / a pin count …) pops as each ball settles.
		// Placed just in front of the pins (NOT back at the throw line): the watch camera eases to a
		// vantage at deck − WatchBack looking down-lane at the deck, so the throw line ends up BEHIND
		// it — the banner has to sit ahead of the pins to be in the settle-time frame.
		var calloutX = headPinX - rowDepth * 1.5f;
		BuildAnnouncerCallout( calloutX, laneHalf, H, ballDiameterUnits );

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
		_pinAudio = Components.GetOrCreate<PinImpactAudio>(); // one bank for the lane; pins funnel here
		Components.GetOrCreate<AnnouncerAudio>(); // one announcer VO bank for the lane (director routes calls here)
		// Compute the rack layout (WORLD home spots — the director respots/scores in world space). Built
		// on EVERY client so the model/audio/scale are ready when the networked pins replicate in, but the
		// pin OBJECTS themselves are spawned once by the host (HostSpawnPins).
		_pinSpots.Clear();
		var n = 1;
		for ( var r = 0; r < 4; r++ )
		{
			var px = headPinX + r * rowDepth;
			for ( var i = 0; i <= r; i++ )
			{
				var py = (i - r * 0.5f) * pinSpace;
				var home = new Vector3( px, py, H ); // base sits on the platform top (lane-local)
				_pinSpots.Add( (n, ToWorld( home )) );
				n++;
			}
		}

		// The settee sits ON the backboard deck behind the pit (deckFrontX is its lane-facing edge,
		// deckTopZ its flat top), extending away from the lane (+x) so the seats face back up-lane and
		// the bowlers watch the ball roll toward them. (cu was computed up by the backboard.)
		SetteeFaceRotation = Transform.World.Rotation * Rotation.FromYaw( 180f );
		BuildSettee( cu, deckFrontX, +1f, deckTopZ, platformHalf );

		// The chase cam that the right-hand settee screen mirrors (renders the active ball to a
		// texture). Built locally on every client — it's a re-render of the local ball, which is the
		// bowler's real ball or the streamed copy spectators see.
		Components.GetOrCreate<LaneMonitorCamera>();
	}

	/// <summary>FIRST assignment of the procedural arrow model to its renderers — in OnStart, with the
	/// renderers left null in OnEnabled. A runtime ModelBuilder model assigned during OnEnabled shows the
	/// purple ERROR model on a joining CLIENT; a fresh null->model assignment in OnStart renders correctly
	/// (the same trick the pins use via Pin.OnStart/ConfigurePin). Host-harmless.</summary>
	protected override void OnStart()
	{
		foreach ( var mr in _arrowRenderers )
			if ( mr.IsValid() ) mr.Model = _arrowModel;
	}

	/// <summary>
	/// The bowlers' settee: the seating area BEHIND the pins, sitting on top of the backboard deck. Lays
	/// down a carpeted riser, two bench seats, the walk-up control podium and the overhead scoring +
	/// camera monitors on posts. Sized to the walking-Citizen bowlers: real inches × the citizen
	/// units-per-inch <paramref name="cu"/> (≈1), so it reads at human scale next to Terry rather than
	/// ballooning to match the giant ball.
	/// <paramref name="frontX"/> is the deck's lane-facing edge; the settee extends AWAY from the lane in
	/// the <paramref name="away"/> direction (±1 in x). <paramref name="baseZ"/> is the deck top it stands
	/// on. The seats face back toward the lane so the bowlers watch the ball roll toward them.
	/// </summary>
	private void BuildSettee( float cu, float frontX, float away, float baseZ, float platformHalf )
	{
		// Board dimensions first — the settee floor grows to seat the (now 2×) boards.
		var boardW = MonitorWidthInches * cu;
		var boardGap = boardW * 0.14f;            // space between the two boards
		var monitorSpan = boardW * 2f + boardGap; // total width the two boards occupy

		var depth = SetteeDepthInches * cu;
		var riser = SetteeRiserInches * cu;
		// Wide enough to seat the boards with a margin, but at least the configured seating width.
		var halfW = MathF.Min(
			MathF.Max( SetteeWidthInches * cu * 0.5f, monitorSpan * 0.5f + 28f * cu ),
			platformHalf );
		var midX = frontX + away * depth * 0.5f;   // settee centre, half its depth back from the lane edge

		// Carpeted riser: a low step on the deck top that reads as the defined seating zone.
		MakeBox( "SetteeCarpet", new Vector3( midX, 0f, baseZ + riser * 0.5f ),
			new Vector3( depth, halfW * 2f, riser ), SetteeCarpetColor );

		// Where the director drops walking bowlers in: on the carpet, mid-settee, facing the lane.
		// Exposed in world space (the director spawns avatars at world positions).
		SetteeFloorCenter = ToWorld( new Vector3( midX, 0f, baseZ + riser ) );
		SetteeHalfWidthUnits = halfW;
		SetteeHalfDepthUnits = depth * 0.5f;

		// Benches down the LEFT and RIGHT edges, facing INWARD (toward the settee centre). Each runs most
		// of the settee depth with its backrest along the outer (±Y) wall edge.
		var seatH = BenchSeatHeightInches * cu;
		var seatD = BenchSeatDepthInches * cu;
		var backH = BenchBackHeightInches * cu;
		var benchLenX = depth * 0.72f;
		var benchEdgeY = halfW - seatD * 0.6f; // a touch in from the wall
		MakeSideBench( "SetteeBenchLeft", midX, -benchEdgeY, +1f, benchLenX, baseZ + riser, seatH, seatD, backH, cu );
		MakeSideBench( "SetteeBenchRight", midX, +benchEdgeY, -1f, benchLenX, baseZ + riser, seatH, seatD, backH, cu );

		// Walk-up control podium + interaction station: stand anywhere on the settee and press E to open
		// the lane controls and free the cursor (TerryAvatar runs the engage). The podium is a visible
		// "desk" so there's something to walk up to; the station's range covers the whole settee. Sits
		// toward the back of the settee (away from the lane edge) so it doesn't block the view.
		var podiumX = frontX + away * depth * 0.84f;
		var podiumH = 40f * cu;
		MakeBox( "ControlPodium", new Vector3( podiumX, 0f, baseZ + riser + podiumH * 0.5f ),
			new Vector3( 18f * cu, 44f * cu, podiumH ), BenchFrameColor );
		MakeDecal( "ControlPodiumTop", new Vector3( podiumX, 0f, baseZ + riser + podiumH + 1f * cu ),
			new Vector3( 20f * cu, 46f * cu, 2f * cu ), MonitorBezelColor );

		var station = new GameObject( true, "LaneStation" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		station.LocalPosition = new Vector3( podiumX, 0f, baseZ + riser );
		station.Components.Create<LaneStation>().InteractRange = MathF.Max( halfW + depth * 0.7f, 200f );

		BuildMonitor( cu, frontX, away, baseZ + riser, boardW, boardGap );
	}

	/// <summary>A bench running along X at a ±Y edge of the settee, facing INWARD. <paramref name="inwardY"/>
	/// is +1 when the settee centre is at +Y of the bench (the left bench) and -1 for the right bench; the
	/// backrest sits on the outer edge. Visual-only (no colliders) — it's furniture. <paramref name="cu"/>
	/// is the citizen units-per-inch the settee is sized in.</summary>
	private void MakeSideBench( string name, float centerX, float centerY, float inwardY, float lengthX, float floorZ, float seatH, float seatD, float backH, float cu )
	{
		var cushionThk = 3f * cu;
		var legH = MathF.Max( seatH - cushionThk, cushionThk ); // frame box under the cushion
		// Frame / legs.
		MakeDecal( $"{name}Frame", new Vector3( centerX, centerY, floorZ + legH * 0.5f ),
			new Vector3( lengthX, seatD, legH ), BenchFrameColor );
		// Cushion.
		MakeDecal( $"{name}Cushion", new Vector3( centerX, centerY, floorZ + legH + cushionThk * 0.5f ),
			new Vector3( lengthX, seatD, cushionThk ), BenchCushionColor );
		// Backrest along the OUTER edge (the side away from the settee centre), rising above the seat.
		var backThk = 2f * cu;
		var backY = centerY - inwardY * (seatD * 0.5f - backThk * 0.5f);
		MakeDecal( $"{name}Back", new Vector3( centerX, backY, floorZ + seatH + backH * 0.5f ),
			new Vector3( lengthX, backThk, backH ), BenchFrameColor );
	}

	/// <summary>The big backstop pin board: a WorldPanel (PinReadout) exactly the lane's width,
	/// floating above the back wall and facing back at the bowler (-x). It shows the same director
	/// pin data as the corner overlay, blown up so the leave is legible all the way from the approach.
	/// <paramref name="frontX"/> is the back wall's bowler-facing face; <paramref name="wallTopZ"/> its top.</summary>
	private void BuildBackstopReadout( float frontX, float laneHalf, float wallTopZ, float D )
	{
		const float s2w = 0.05f;                           // WorldPanel.ScreenToWorldScale
		// PORTRAIT canvas: header + count line stacked over the four-row rack triangle. CRITICAL: a
		// WorldPanel's root has Scale == 2, so the CSS layout area is PanelSize / 2 — the old 760×1320
		// board only gave the UI ~380×660 css px, which the ~648×880 content overflowed on BOTH axes
		// (back row clipped to its middle pins, title + head pin clipped off top/bottom). Size the
		// canvas to ~2× the content so it fits with margin. worldW scales by the SAME factor as
		// panelPx.x, so `scale` (and therefore the circle world-size we like) is unchanged — only the
		// canvas grew. The board ends up ~2.9× the lane width, big and legible from the approach.
		var panelPx = new Vector2( 1400f, 1900f );
		var worldW = laneHalf * 2f * 1.6f * (1400f / 760f); // same px:world ratio as the old board → same circle size
		var scale = worldW / (panelPx.x * s2w);            // GO scale so the panel fills the board (RenderScale stays 1)
		var worldH = panelPx.y * s2w * scale;
		// Float it clear above the backstop AND lift it well overhead so it stops sinking into the back
		// drop/wall — it should hang high, fully legible from the approach.
		var centerZ = wallTopZ + worldH * 0.5f + 0.2f * D + 700f;

		// Dark bezel slab just behind the panel for contrast against the alley.
		var bezelThk = 0.1f * D;
		var bezelPad = 0.12f * D;
		MakeDecal( "BackstopBezel", new Vector3( frontX + bezelThk * 0.5f, 0f, centerZ ),
			new Vector3( bezelThk, worldW + bezelPad * 2f, worldH + bezelPad * 2f ), MonitorBezelColor );

		// The WorldPanel itself, facing -x (toward the bowler). A PinReadout razor rides it.
		var screen = new GameObject( true, "BackstopReadoutScreen" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		screen.LocalPosition = new Vector3( frontX - 0.02f * D, 0f, centerZ );
		screen.LocalRotation = Rotation.FromYaw( 180f ); // identity WorldPanel faces +x; flip to face the approach
		screen.LocalScale = scale;

		var panel = screen.Components.Create<WorldPanel>();
		panel.PanelSize = panelPx;
		panel.RenderScale = 1f;
		panel.LookAtCamera = false;
		panel.InteractionRange = 100000f;

		screen.Components.Create<PinReadout>();
	}

	/// <summary>The announcer call-out banner: a WorldPanel (<see cref="AnnouncerCallout"/>) exactly the
	/// lane's width, floating over the lane and tilted back 45° to face the (elevated) watch camera. It
	/// shows the director's current call (STRIKE!, SPLIT!, a spoken pin count, …) as each ball settles.
	/// <paramref name="calloutX"/> is the lane-local x to float it at (just in front of the pins, so it's
	/// in the settle-time watch frame); <paramref name="surfaceZ"/> the lane surface top; <paramref
	/// name="D"/> the ball diameter (the lane's size unit).</summary>
	private void BuildAnnouncerCallout( float calloutX, float laneHalf, float surfaceZ, float D )
	{
		const float s2w = 0.05f;                           // WorldPanel.ScreenToWorldScale
		// Wide, short banner. CRITICAL: a WorldPanel's root has Scale == 2, so the CSS layout area is
		// PanelSize / 2 — size the canvas ~2× the content so big text fits with margin (see BuildBackstopReadout).
		var panelPx = new Vector2( 1800f, 600f );
		var worldW = laneHalf * 2f;                        // exactly the lane width
		var scale = worldW / (panelPx.x * s2w);            // GO scale so the panel fills the lane width (RenderScale stays 1)
		var worldH = panelPx.y * s2w * scale;
		// Float it above the pins, roughly at the watch camera's eye-line (the cam sits at deck + height
		// looking down at the deck), so the banner sits in frame when the call fires.
		var centerZ = surfaceZ + worldH * 0.5f + 1.3f * D;

		var screen = new GameObject( true, "AnnouncerCalloutScreen" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		screen.LocalPosition = new Vector3( calloutX, 0f, centerZ );
		// identity WorldPanel faces +x; yaw 180 turns it back up-lane toward the bowler/camera, pitch
		// leans the top away down-lane so it's tilted back 45° from the approach view.
		screen.LocalRotation = Rotation.FromYaw( 180f ) * Rotation.FromPitch( 45f );
		screen.LocalScale = scale;

		var panel = screen.Components.Create<WorldPanel>();
		panel.PanelSize = panelPx;
		panel.RenderScale = 1f;
		panel.LookAtCamera = false;
		panel.InteractionRange = 100000f;

		screen.Components.Create<AnnouncerCallout>();
	}

	/// <summary>The two overhead settee screens, side by side facing the seats. From the seated bowler's
	/// point of view (they face the lane) the LEFT screen is the live CAMERA FEED of whoever's up (a chase
	/// cam of the active ball rendered to a texture — see <see cref="LaneMonitorCamera"/> / MonitorFeed)
	/// and the RIGHT screen is this lane's SCOREBOARD (LaneConsole). <paramref name="away"/> is the
	/// direction the settee runs away from the lane (±1 in x): the seats face the lane (−away), so the
	/// camera (bowler's LEFT) goes to −away·offsetY. Each is ~half the settee width so they don't overlap.</summary>
	private void BuildMonitor( float cu, float frontX, float away, float floorZ, float boardW, float boardGap )
	{
		var centerZ = floorZ + MonitorCenterHeightInches * cu;
		var offsetY = (boardW + boardGap) * 0.5f; // each board half a span + gap off centre

		// Ball camera on the bowler's LEFT, scoreboard on their RIGHT — which Y that is depends on which
		// way the seats face (−away).
		BuildPanelScreen( "LaneFeedScreen", cu, frontX, away, -away * offsetY, centerZ, boardW,
			new Vector2( 1280f, 720f ) ).Components.Create<MonitorFeed>();

		BuildPanelScreen( "LaneScoreScreen", cu, frontX, away, away * offsetY, centerZ, boardW,
			new Vector2( 1200f, 675f ) ).Components.Create<LaneConsole>();
	}

	/// <summary>Build one bezelled WorldPanel screen on support posts at the settee, facing the seats, and
	/// return its GameObject so the caller can mount the razor panel it should display. Citizen-scale
	/// (<paramref name="cu"/> = citizen units-per-inch); the board is <paramref name="boardW"/> wide.
	/// <paramref name="away"/> is the settee's away-from-lane direction (±1): the panel sits at the lane
	/// edge facing the seats (+away), with its bezel/posts behind it on the lane side.</summary>
	private GameObject BuildPanelScreen( string name, float cu, float frontX, float away, float centerY, float centerZ, float boardW, Vector2 panelPx )
	{
		const float s2w = 0.05f; // WorldPanel.ScreenToWorldScale — world units per panel pixel at scale 1
		// The panel's world footprint is PanelSize·s2w·WorldScale (RenderScale does NOT change it), so we
		// SCALE the panel GameObject to make it exactly boardW wide and keep RenderScale = 1: full-resolution
		// UI that fills the panel. (The old code drove RenderScale instead, which left the panel half the
		// bezel width and blew the text up.)
		var scale = boardW / (panelPx.x * s2w);
		var worldW = boardW;
		var worldH = panelPx.y * s2w * scale; // = boardW · (panelPx.y / panelPx.x)
		var screenX = frontX + away * 6f * cu;  // just inside the settee's lane edge, overhead the seats
		var faceYaw = away > 0f ? 0f : 180f;    // identity WorldPanel faces +x; aim its normal at the seats (+away)

		var bezelThk = 2.5f * cu;
		var bezelPad = 2f * cu;

		// Bezel slab a touch larger than the panel, framing it — behind the panel on the lane side (−away).
		MakeDecal( $"{name}Bezel", new Vector3( screenX - away * bezelThk * 0.5f, centerY, centerZ ),
			new Vector3( bezelThk, worldW + bezelPad * 2f, worldH + bezelPad * 2f ), MonitorBezelColor );

		// Support posts: legs that stop at the BOTTOM of the board, so they don't run up behind the panel.
		var boardBottomZ = centerZ - worldH * 0.5f - bezelPad;
		var postW = 3f * cu;
		var postSpanY = worldW * 0.5f - postW;
		foreach ( var sy in new[] { -postSpanY, postSpanY } )
		{
			MakeDecal( $"{name}Post{(sy < 0 ? "L" : "R")}",
				new Vector3( screenX - away * bezelThk, centerY + sy, boardBottomZ * 0.5f ),
				new Vector3( postW, postW, boardBottomZ ), MonitorPostColor );
		}

		// The WorldPanel itself, facing the seats (+away). LocalScale is what sizes the board.
		var screen = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		screen.LocalPosition = new Vector3( screenX + away * 0.5f * cu, centerY, centerZ );
		screen.LocalRotation = Rotation.FromYaw( faceYaw );
		screen.LocalScale = scale;

		var panel = screen.Components.Create<WorldPanel>();
		panel.PanelSize = panelPx;
		panel.RenderScale = 1f;
		panel.LookAtCamera = false;
		panel.InteractionRange = 100000f;
		return screen;
	}

	// Push a lane-local point out into world space through the lane root transform. Used to expose the
	// landmarks (run-up, pin deck, throw line, settee) the ball/camera/director compare against world
	// positions, so they stay correct when a lane is dropped at an offset.
	private Vector3 ToWorld( Vector3 local ) => Transform.World.PointToWorld( local );

	// Static box with collider on the (uniformly-scaled) root and the visual on a non-uniformly
	// scaled child (s&box freezes physics if a BoxCollider rides a non-uniform transform scale).
	// A rotation, if given, is applied to the root so the collider tilts with the visual (ramp).
	// Positions are LANE-LOCAL (relative to this builder's GameObject = the lane root): the whole lane
	// rides its root transform, so a second lane is just the same subtree dropped at another spot.
	private void MakeBox( string name, Vector3 localPos, Vector3 size, Color tint, Rotation? rot = null,
		float? elasticity = null, float? friction = null )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.LocalPosition = localPos;
		if ( rot.HasValue ) go.LocalRotation = rot.Value;

		var col = go.Components.GetOrCreate<BoxCollider>();
		col.Scale = size;
		col.Elasticity = elasticity ?? LaneElasticity; // bouncy deck/walls so pins ricochet (cranked for chaos)
		if ( friction.HasValue ) col.Friction = friction.Value;

		var visual = new GameObject( true, name + "Visual" ) { Parent = go };
		var mr = visual.Components.GetOrCreate<ModelRenderer>();
		mr.Model = Model.Load( "models/dev/box.vmdl" );
		mr.Tint = tint;

		var b = mr.Model.Bounds.Size;
		visual.LocalScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );
	}

	// Visual-only thin slab (no collider) laid on a surface — boards, foul line, etc. Lane-local pos.
	private void MakeDecal( string name, Vector3 localPos, Vector3 size, Color tint )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.LocalPosition = localPos;

		var mr = go.Components.GetOrCreate<ModelRenderer>();
		mr.Model = Model.Load( "models/dev/box.vmdl" );
		mr.Tint = tint;

		var b = mr.Model.Bounds.Size;
		go.LocalScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );
	}

	// One of the seven targeting arrows: a flat triangle decal pointing back at the bowler. Lane-local.
	private void MakeArrow( string name, Vector3 localPos )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		// Build the arrows purely client-local. Every client builds the lane geometry itself in OnEnabled,
		// so this GO must NOT replicate: a runtime child of the networked lane root defaults to
		// NetworkMode.Snapshot and replicates inside the lane's snapshot. The arrow's PROCEDURAL model can't
		// serialize, so that replicated copy arrives as the purple ERROR model — overlapping the correct one
		// we build here. NetworkMode.Never kills the replicated copy. (Boards dodge this only because their
		// model is a loadable asset path; the procedural arrows can't.)
		go.NetworkMode = NetworkMode.Never;
		go.LocalPosition = localPos;
		var mr = go.Components.GetOrCreate<ModelRenderer>();
		mr.Tint = ArrowColor;
		// NB: the model is set in OnStart, NOT here. Assigning the procedural model now (OnEnabled) leaves
		// a client renderer showing the ERROR model, and re-setting the SAME reference later is a no-op
		// (the setter short-circuits). Leaving it null here so OnStart's assign is a real null->model
		// change — exactly how the pins (ConfigurePin) dodge it.
		_arrowRenderers.Add( mr );
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
	/// of the belly diameter). Points are the official USBC contour: the manual fixes the
	/// diameter at twelve measured heights (base 2.03", belly 4.77" at 4.5", neck 1.78" at 10",
	/// head 2.55" at 13.5"), here divided by the 4.766" belly to make the "1.0" reference. The
	/// crown dome is added on top. Revolving this gives the true bowling-pin shape, and because
	/// the solid is built straight from the curve the belly's volume sits low and the mass
	/// distribution (centre of gravity ~5.3" up) is realistic with no manual tweak.
	/// </summary>
	private static readonly (float t, float dia)[] PinSilhouette =
	{
		(0.000f, 0.426f), // base — 2.03"
		(0.050f, 0.594f), // 0.75"
		(0.150f, 0.820f), // 2.25"
		(0.225f, 0.946f), // 3-3/8"
		(0.300f, 1.000f), // 4-1/2" belly — widest (4.77")
		(0.392f, 0.957f), // 5-7/8"
		(0.575f, 0.518f), // 8-5/8"
		(0.625f, 0.413f), // 9-3/8"
		(0.667f, 0.374f), // 10" neck — narrowest (1.78")
		(0.725f, 0.392f), // 10-7/8"
		(0.783f, 0.439f), // 11-3/4"
		(0.900f, 0.535f), // 13-1/2" head — widest crown (2.55")
		(0.955f, 0.380f), // crown dome
		(1.000f, 0.165f), // rounded top (kept >0 so the cap isn't degenerate)
	};

	// A pin instance: Rigidbody + ModelRenderer + ModelCollider, all pointing at the one shared
	// lathed pin Model. The ModelCollider's convex hulls feed the Rigidbody's mass build, so the
	// inertia tensor and CoM come from the true pin solid.
	/// <summary>HOST ONLY: spawn this lane's ten pins as networked objects on their home spots, once.
	/// Each is parented into the lane subtree (so <c>Lane.Of</c> resolves it), tagged Takeover so the
	/// active bowler can assume ownership for their turn, and network-spawned host-owned. The render/
	/// collision mesh + physics are attached per-client in <see cref="ConfigurePin"/> (the procedural
	/// model can't replicate). Idempotent — guarded by <see cref="PinsSpawned"/>.</summary>
	public void HostSpawnPins()
	{
		if ( PinsSpawned || !Networking.IsHost ) return;
		if ( !_built ) OnEnabled();
		if ( _pinSpots.Count == 0 ) return;

		foreach ( var (number, home) in _pinSpots )
		{
			// Root-spawned (NOT parented into the lane): a networked object under a non-networked scene
			// parent fails to replicate to late-joiners. The Pin finds its lane via Lane.Local instead.
			var go = new GameObject( true, $"Pin{number}" ) { Flags = GameObjectFlags.NotSaved };
			go.WorldPosition = home;
			go.NetworkMode = NetworkMode.Object;

			var pin = go.Components.Create<Pin>();
			pin.Number = number;
			pin.Home = home;

			go.Network.SetOwnerTransfer( OwnerTransfer.Takeover ); // the bowler grabs the rack for their turn
			go.NetworkSpawn(); // host-owned to start; replicates to every client
		}
		PinsSpawned = true;
	}

	/// <summary>Attach the lane's local procedural mesh + the pin's physics/audio to a (possibly just-
	/// replicated) pin. Runs on EVERY client from <see cref="Pin.OnStart"/>: it re-skins proxy pins off
	/// the ERROR model and arms the collider/rigidbody so a pin is ready the instant its owner simulates.</summary>
	public void ConfigurePin( Pin pin )
	{
		if ( !pin.IsValid() ) return;
		if ( !_built ) OnEnabled();
		var go = pin.GameObject;

		// Rigidbody first; MassOverride after the collider (adding the shape rebuilds mass from
		// volume via BuildMass, which would otherwise clobber the override).
		var rb = go.Components.GetOrCreate<Rigidbody>();

		var mr = go.Components.GetOrCreate<ModelRenderer>();
		mr.Model = _pinModel; // the procedural lathe — null on a fresh proxy, so set it here
		mr.Tint = PinColor;

		var col = go.Components.GetOrCreate<ModelCollider>();
		col.Model = _pinModel;
		col.Friction = PinFriction; // smooth plastic-coated pin slides & scatters
		col.Elasticity = PinElasticity; // springy maple — pins launch & cartwheel off ball and each other

		rb.MassOverride = PinMass;
		// CCD stays OFF on pins: the engine skips CCD between two bodies that BOTH have EnhancedCcd
		// (Rigidbody.cs doc), so enabling it here actually DISABLED ball↔pin CCD and let the fast ball
		// sweep through. The ball keeps EnhancedCcd; with pins discrete, ball-vs-pin CCD runs again.
		rb.EnhancedCcd = false;
		rb.GravityScale = _pinGravityScale; // scale-correct 1g at lane size × feel knob (pins only)

		// Silence the engine's built-in surface collision sound — our procedural crash audio replaces
		// it. CollisionSoundSystem skips a contact if EITHER body has this flag, so flagging the pins
		// covers pin↔pin, pin↔ball and pin↔lane.
		rb.RigidbodyFlags |= RigidbodyFlags.DisableCollisionSounds;

		// Procedural crash audio: this listener routes the pin's collisions to the shared bank.
		go.Components.GetOrCreate<PinImpactListener>().Bank = _pinAudio;
	}

	// The two red neck stripes, as height fractions (t) up the pin. Only the RENDER mesh is split
	// on these — collision still uses the coarse PinSilhouette, so mass/inertia are untouched.
	private static readonly (float lo, float hi)[] PinStripeBands = { (0.69f, 0.73f), (0.78f, 0.82f) };

	// Monotone-cubic (Fritsch–Carlson) tangents through PinSilhouette. A linear interpolation of
	// the sparse spec points reads as a stack of cones; a monotone cubic passes through every spec
	// diameter yet curves smoothly between them and — unlike a plain Catmull-Rom — never overshoots,
	// so the render lathe never bulges past the belly or pinches below the neck.
	private static readonly float[] _silSlope = BuildSilhouetteSlopes();

	private static float[] BuildSilhouetteSlopes()
	{
		var s = PinSilhouette;
		var n = s.Length;
		var d = new float[n - 1]; // secant slope of each segment
		for ( var i = 0; i < n - 1; i++ )
			d[i] = (s[i + 1].dia - s[i].dia) / (s[i + 1].t - s[i].t);

		var m = new float[n];
		m[0] = d[0];
		m[n - 1] = d[n - 2];
		for ( var i = 1; i < n - 1; i++ )
			m[i] = (d[i - 1] * d[i] <= 0f) ? 0f : (d[i - 1] + d[i]) * 0.5f; // flat at extrema (belly/neck)

		// Limiter: clamp tangents into the monotone region so no segment overshoots its endpoints.
		for ( var i = 0; i < n - 1; i++ )
		{
			if ( d[i] == 0f ) { m[i] = 0f; m[i + 1] = 0f; continue; }
			var a = m[i] / d[i];
			var b = m[i + 1] / d[i];
			var h = a * a + b * b;
			if ( h > 9f )
			{
				var tau = 3f / MathF.Sqrt( h );
				m[i] = tau * a * d[i];
				m[i + 1] = tau * b * d[i];
			}
		}
		return m;
	}

	// Diameter (as a fraction of the belly) at an arbitrary height t, along the smooth monotone-cubic
	// PinSilhouette — lets the render mesh resample the profile as finely as it likes.
	private static float DiaAt( float t )
	{
		var s = PinSilhouette;
		if ( t <= s[0].t ) return s[0].dia;
		if ( t >= s[^1].t ) return s[^1].dia;

		var i = 1;
		while ( i < s.Length && t > s[i].t ) i++;
		var t0 = s[i - 1].t; var t1 = s[i].t;
		var hgap = t1 - t0;
		var x = (t - t0) / hgap;
		var y0 = s[i - 1].dia; var y1 = s[i].dia;
		var m0 = _silSlope[i - 1] * hgap; var m1 = _silSlope[i] * hgap; // Hermite tangents (curve units)

		var x2 = x * x; var x3 = x2 * x;
		return (2f * x3 - 3f * x2 + 1f) * y0 + (x3 - 2f * x2 + x) * m0
			+ (-2f * x3 + 3f * x2) * y1 + (x3 - x2) * m1;
	}

	// The pins' glossy-white body material and the red-tinted copy for the stripes. Both ride the
	// complex shader (same as white.vmat) with the roughness pulled down for a slight sheen; colour
	// lives on the material (g_vColorTint), so the two submeshes can differ under one ModelRenderer.
	private void EnsurePinMaterials()
	{
		if ( _pinMat is not null ) return;
		var baseMat = Material.Load( "materials/default/white.vmat" );
		_pinMat = baseMat.CreateCopy();
		_pinMat.Set( "g_flRoughnessScaleFactor", 0.45f ); // < 0.5 default → a soft plastic sheen
		_pinMat.Set( "g_flMinRoughness", 0.05f );
		_pinStripeMat = baseMat.CreateCopy();
		_pinStripeMat.Set( "g_flRoughnessScaleFactor", 0.45f );
		_pinStripeMat.Set( "g_flMinRoughness", 0.05f );
		_pinStripeMat.Set( "g_vColorTint", PinStripeColor );
	}

	// Build the shared pin Model by revolving PinSilhouette around the vertical axis: a smooth
	// render mesh (split white body + red stripes), plus one convex-hull frustum per profile slab
	// for simulatable collision (a single hull can't capture the concave neck, but a stack can).
	private Model BuildPinModel( float u )
	{
		EnsurePinMaterials();

		var height = PinHeightInches * u;
		var bellyRadius = PinWidthInches * u * 0.5f; // belly diameter is the profile's "1.0"
		const int radial = 48;                       // segments around the circumference (smooth lathe)

		// --- Render profile: the smooth monotone-cubic silhouette resampled at a high, uniform ring
		//     count, plus exact rings at every spec point and each stripe edge. The dense sampling +
		//     curved interpolation makes the body read as a continuous surface, not stacked cones, and
		//     keeps the two red bands on their own material. Far finer than the collision rings. ---
		const int smoothRings = 72;
		var ts = PinSilhouette.Select( p => p.t ).ToList();
		foreach ( var band in PinStripeBands ) { ts.Add( band.lo ); ts.Add( band.hi ); }
		for ( var i = 0; i <= smoothRings; i++ ) ts.Add( i / (float)smoothRings );
		ts = ts.Distinct().OrderBy( x => x ).ToList();
		var fineRings = ts.Count;

		var fz = new float[fineRings];
		var fr = new float[fineRings];
		for ( var i = 0; i < fineRings; i++ ) { fz[i] = ts[i] * height; fr[i] = DiaAt( ts[i] ) * bellyRadius; }

		// One position + outward-normal ring per fine sample (normal = profile tangent rotated -90°).
		var ringPos = new Vector3[fineRings][];
		var ringNrm = new Vector3[fineRings][];
		var ringTan = new Vector3[fineRings][];
		for ( var i = 0; i < fineRings; i++ )
		{
			var dz = (i < fineRings - 1 ? fz[i + 1] : fz[i]) - (i > 0 ? fz[i - 1] : fz[i]);
			var dr = (i < fineRings - 1 ? fr[i + 1] : fr[i]) - (i > 0 ? fr[i - 1] : fr[i]);
			var nPlane = new Vector2( dz, -dr ).Normal; // (radial, axial)

			ringPos[i] = new Vector3[radial];
			ringNrm[i] = new Vector3[radial];
			ringTan[i] = new Vector3[radial];
			for ( var j = 0; j < radial; j++ )
			{
				var a = j / (float)radial * MathF.PI * 2f;
				var c = MathF.Cos( a );
				var s = MathF.Sin( a );
				ringPos[i][j] = new Vector3( fr[i] * c, fr[i] * s, fz[i] );
				ringNrm[i][j] = new Vector3( nPlane.x * c, nPlane.x * s, nPlane.y ).Normal;
				// Circumferential tangent (dPos/dθ). A real, non-zero tangent is REQUIRED: the lit
				// shader builds its TBN from it, and a zero tangent makes that basis NaN — which is
				// what left the pins unlit / unable to take a shadow.
				ringTan[i][j] = new Vector3( -s, c, 0f );
			}
		}

		// White body and red stripes go to separate vertex buffers (= separate submeshes/materials).
		// Each slab appends its own two rings so a slab can be assigned a colour independently; the
		// smooth per-ring normals keep the seams invisible.
		var vbWhite = new VertexBuffer(); vbWhite.Init( true );
		var vbRed = new VertexBuffer(); vbRed.Init( true );
		var whiteCount = 0; var redCount = 0;

		for ( var i = 0; i < fineRings - 1; i++ )
		{
			var mid = (ts[i] + ts[i + 1]) * 0.5f;
			var red = PinStripeBands.Any( b => mid >= b.lo && mid <= b.hi );
			var vb = red ? vbRed : vbWhite;
			var baseI = red ? redCount : whiteCount;

			for ( var j = 0; j < radial; j++ )
				vb.Add( new Vertex( ringPos[i][j], ringNrm[i][j], ringTan[i][j], new Vector4( j / (float)radial, ts[i], 0f, 0f ) ) );
			for ( var j = 0; j < radial; j++ )
				vb.Add( new Vertex( ringPos[i + 1][j], ringNrm[i + 1][j], ringTan[i + 1][j], new Vector4( j / (float)radial, ts[i + 1], 0f, 0f ) ) );

			for ( var j = 0; j < radial; j++ )
			{
				var jn = (j + 1) % radial;
				var a = baseI + j;
				var b = baseI + jn;
				var cc = baseI + radial + j;
				var d = baseI + radial + jn;
				// Outward-facing winding (the old build was inside-out — backface culling ate the
				// outer surface and you saw straight through the pin / its open underside).
				vb.AddRawIndex( a ); vb.AddRawIndex( b ); vb.AddRawIndex( cc );
				vb.AddRawIndex( b ); vb.AddRawIndex( d ); vb.AddRawIndex( cc );
			}

			if ( red ) redCount += radial * 2; else whiteCount += radial * 2;
		}

		// Bottom and top caps close the solid; both ride the white body buffer.
		whiteCount += AddCap( vbWhite, ringPos[0], fz[0], radial, whiteCount, faceDown: true );
		AddCap( vbWhite, ringPos[fineRings - 1], fz[fineRings - 1], radial, whiteCount, faceDown: false );

		var meshWhite = new Mesh( _pinMat );
		meshWhite.CreateBuffers( vbWhite );
		var meshRed = new Mesh( _pinStripeMat );
		meshRed.CreateBuffers( vbRed );

		var builder = new ModelBuilder();
		builder.AddMesh( meshWhite );
		builder.AddMesh( meshRed );

		// Collision: a convex hull per COARSE silhouette slab. Convex per slab handles the concave
		// neck a single hull would fill in; the stack closes the whole solid. (Unchanged by the
		// render-mesh stripe split, so mass + inertia stay exactly as tuned.)
		// Hulls stay at a COARSE ring count, independent of the render lathe: the convex COOKER is
		// far fussier than the mesh builder, and a 96-point-per-slab hull (render radial) can be
		// rejected at cook time — which silently drops the pin's physics shapes and lets the ball
		// pass straight through. A near-cylindrical pin needs nothing finer than this anyway.
		const int hullRadial = 24;
		var rings = PinSilhouette.Length;
		for ( var i = 0; i < rings - 1; i++ )
		{
			var hull = new List<Vector3>( hullRadial * 2 );
			for ( var k = i; k <= i + 1; k++ )
			{
				var rr = PinSilhouette[k].dia * bellyRadius;
				var zz = PinSilhouette[k].t * height;
				for ( var j = 0; j < hullRadial; j++ )
				{
					var a = j / (float)hullRadial * MathF.PI * 2f;
					hull.Add( new Vector3( rr * MathF.Cos( a ), rr * MathF.Sin( a ), zz ) );
				}
			}
			builder.AddCollisionHull( hull );
		}
		builder.WithMass( PinMass );
		return builder.Create();
	}

	// Adds a fan cap over <paramref name="ring"/> centred on the axis, with its first vertex at
	// absolute index <paramref name="firstIndex"/>; returns how many vertices it added.
	private static int AddCap( VertexBuffer vb, Vector3[] ring, float zc, int radial, int firstIndex, bool faceDown )
	{
		var normal = faceDown ? Vector3.Down : Vector3.Up;
		// A flat cap lies in the XY plane, so +X is a valid in-plane tangent (perpendicular to the
		// up/down normal). Non-zero tangents keep the lit shader's TBN basis from going NaN.
		var tangent = Vector3.Forward;
		var centerIdx = firstIndex;
		vb.Add( new Vertex( new Vector3( 0f, 0f, zc ), normal, tangent, new Vector4( 0.5f, 0.5f, 0f, 0f ) ) );

		var ringStart = firstIndex + 1;
		for ( var j = 0; j < radial; j++ )
			vb.Add( new Vertex( ring[j], normal, tangent, new Vector4( j / (float)radial, 0f, 0f, 0f ) ) );

		for ( var j = 0; j < radial; j++ )
		{
			var a = ringStart + j;
			var b = ringStart + (j + 1) % radial;
			// Wind so the visible face points the way the normal does (outward, matching the sides).
			if ( faceDown ) { vb.AddRawIndex( centerIdx ); vb.AddRawIndex( b ); vb.AddRawIndex( a ); }
			else { vb.AddRawIndex( centerIdx ); vb.AddRawIndex( a ); vb.AddRawIndex( b ); }
		}

		return 1 + radial;
	}
}
