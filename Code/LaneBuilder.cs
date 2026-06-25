using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Builds a bowling lane at runtime, scaled so the ball reads as a real bowling ball.
///
/// The ball's actual size in units is treated as <see cref="BallDiameterInches"/> (8.5"),
/// which gives a units-per-inch scale used for every real dimension below: two cube walls
/// a lane-width apart, and 10 lathed bowling pins (each a Rigidbody, all sharing one runtime
/// pin Model) arranged in the regulation triangle at the foul-line-to-head-pin distance.
///
/// The pin Model is a surface-of-revolution: a smooth render mesh plus a stack of convex-hull
/// frustums for collision. The physics engine derives the centre of mass AND the full inertia
/// tensor from that true solid, so a uniform-density pin naturally lands its CoM low (~0.36 of
/// its height, like a real pin) and crashes with realistic pin action — no primitive-stack
/// approximation or hand-tuned mass-centre needed.
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
	[Property, Group( "Pins" )] public Color PinColor { get; set; } = new Color( 0.95f, 0.95f, 0.95f, 1f );

	private bool _built;
	private Model _pinModel; // shared by all ten pins; built once at the lane's scale

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
		_pinModel = BuildPinModel( u );
		var n = 1;
		for ( var r = 0; r < 4; r++ )
		{
			var px = headPinX + r * rowDepth;
			for ( var i = 0; i <= r; i++ )
			{
				var py = (i - r * 0.5f) * pinSpace;
				// Pin origin sits on the lane (base at z = 0); the lathed model builds upward.
				MakePin( $"Pin{n}", new Vector3( px, py, 0f ) );
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
	/// Regulation pin silhouette as (height fraction up from the base, diameter as a fraction
	/// of the belly diameter). Revolving this profile gives the classic bowling-pin shape:
	/// narrow base, bulging belly (the widest point, = 1.0), pinched neck, flared head, domed
	/// top. Because the solid is built straight from this curve, the belly's volume sits low and
	/// the mass distribution is realistic without any manual centre-of-mass tweak.
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
	private void MakePin( string name, Vector3 worldPos )
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

		rb.MassOverride = PinMass;
		rb.EnhancedCcd = true; // struck at bowling speed — don't let the ball tunnel through
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
