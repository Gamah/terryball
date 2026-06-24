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
	[Property, Group( "Pins" )] public float PinWidthInches { get; set; } = 4.766f; // belly (widest) diameter
	[Property, Group( "Pins" )] public float PinHeightInches { get; set; } = 15f;
	/// <summary>Mass in kg. A regulation bowling pin is ~1.6 kg (3 lb 8 oz).</summary>
	[Property, Group( "Pins" )] public float PinMass { get; set; } = 1.6f;
	/// <summary>Pin-to-lane kinetic friction. Real pins have a smooth plastic coating
	/// (nylon/Surlyn/urethane) engineered to slide; lane CoF is ~0.18, so ~0.1–0.18 is
	/// realistic. Low = more slide/scatter on impact.</summary>
	[Property, Group( "Pins" )] public float PinFriction { get; set; } = 0.15f;
	/// <summary>Height of the centre of mass as a fraction of pin height. A regulation pin
	/// is deliberately bottom-heavy — its CoM sits ~36% up from the base, which is what makes
	/// a struck pin tip, wobble, and deflect into its neighbours ("pin action") instead of
	/// flying off like a uniform block.</summary>
	[Property, Group( "Pins" )] public float PinMassCenterFrac { get; set; } = 0.34f;
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
				// Pin origin sits on the lane (base at z = 0); the profile builds upward.
				MakePin( $"Pin{n}", new Vector3( px, py, 0f ), u );
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

		// Rigidbody before the collider, MassOverride after — adding the collider rebuilds
		// mass from volume (BuildMass) and would otherwise clobber the override.
		Rigidbody rb = null;
		if ( isDynamic )
			rb = go.Components.GetOrCreate<Rigidbody>();

		var col = go.Components.GetOrCreate<BoxCollider>();
		col.Scale = size;

		if ( rb is not null )
		{
			rb.MassOverride = PinMass;  // light pin vs heavy ball, so the ball plows through
			col.Friction = PinFriction; // slide & scatter instead of gripping the floor
		}

		var visual = new GameObject( true, name + "Visual" ) { Parent = go };
		var mr = visual.Components.GetOrCreate<ModelRenderer>();
		mr.Model = Model.Load( "models/dev/box.vmdl" );
		mr.Tint = tint;

		var b = mr.Model.Bounds.Size;
		visual.LocalScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );
	}

	/// <summary>
	/// The rounded body of the pin as stacked sphere segments: (height up from base, radius),
	/// both as fractions — height of <see cref="PinHeightInches"/>, radius of the belly
	/// half-width (<see cref="PinWidthInches"/>/2). Bulging belly low down, narrow neck, flared
	/// head — a rounded body that topples and scatters like a real pin rather than a cube. The
	/// flat foot underneath (see <see cref="MakePin"/>) is what it actually stands on.
	/// </summary>
	private static readonly (float h, float r)[] PinProfile =
	{
		(0.30f, 1.00f), // belly — widest point
		(0.52f, 0.56f), // waist
		(0.70f, 0.44f), // neck — narrowest
		(0.86f, 0.62f), // head — flared top
	};

	/// <summary>Flat base diameter as a fraction of belly diameter. A regulation pin's base
	/// (~2.06") is ~0.43 of the belly (~4.77"); this small flat foot is the support polygon
	/// the pin stands on and tips about.</summary>
	private const float PinBaseFrac = 0.43f;

	// A bowling pin: one Rigidbody carrying a flat box foot plus a stack of sphere colliders
	// shaped like a pin body, with the centre of mass forced low so it stands stably yet crashes
	// with realistic pin action. Colliders parent to the body; visuals trace the same profile.
	private void MakePin( string name, Vector3 worldPos, float u )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;

		var pinH = PinHeightInches * u;
		var halfW = PinWidthInches * u * 0.5f; // belly half-width drives every segment radius

		// Rigidbody first; mass params after the colliders (each added shape rebuilds mass from
		// volume via BuildMass, which would otherwise clobber the overrides below).
		var rb = go.Components.GetOrCreate<Rigidbody>();

		// Flat foot: a short box on the floor giving a real support polygon — stable standing,
		// tips once the (low) centre of mass crosses its edge.
		var footW = PinWidthInches * u * PinBaseFrac;
		var footH = pinH * 0.18f; // reaches up to meet the belly sphere
		var foot = go.Components.Create<BoxCollider>();
		foot.Center = new Vector3( 0f, 0f, footH * 0.5f );
		foot.Scale = new Vector3( footW, footW, footH );
		foot.Friction = PinFriction;

		var boxBounds = Model.Load( "models/dev/box.vmdl" ).Bounds.Size;
		var footVisual = new GameObject( true, $"{name}Foot" ) { Parent = go };
		footVisual.LocalPosition = new Vector3( 0f, 0f, footH * 0.5f );
		var footMr = footVisual.Components.GetOrCreate<ModelRenderer>();
		footMr.Model = Model.Load( "models/dev/box.vmdl" );
		footMr.Tint = PinColor;
		footVisual.LocalScale = new Vector3( footW / boxBounds.x, footW / boxBounds.y, footH / boxBounds.z );

		var sphereBounds = Model.Load( "models/dev/sphere.vmdl" ).Bounds.Size.x; // dev sphere ≈ 64 across
		foreach ( var (h, r) in PinProfile )
		{
			var z = h * pinH;
			var radius = r * halfW;

			var col = go.Components.Create<SphereCollider>();
			col.Center = new Vector3( 0f, 0f, z );
			col.Radius = radius;
			col.Friction = PinFriction;

			var visual = new GameObject( true, $"{name}Seg" ) { Parent = go };
			visual.LocalPosition = new Vector3( 0f, 0f, z );
			var mr = visual.Components.GetOrCreate<ModelRenderer>();
			mr.Model = Model.Load( "models/dev/sphere.vmdl" );
			mr.Tint = PinColor;
			visual.LocalScale = new Vector3( (radius * 2f) / sphereBounds );
		}

		rb.MassOverride = PinMass;
		rb.EnhancedCcd = true; // struck at bowling speed — don't let the ball tunnel through
		// Drop the centre of mass to the realistic bottom-heavy point: this, not the shape alone,
		// is what gives the tip-wobble-deflect "pin action".
		rb.OverrideMassCenter = true;
		rb.MassCenterOverride = new Vector3( 0f, 0f, PinMassCenterFrac * pinH );
	}
}
