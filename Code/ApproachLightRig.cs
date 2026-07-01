using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// Builds four coloured light posts at the corners of the ball's approach/spawn area (R, G, B, W). Each post
/// is a slim box "pole" with a box "head" aimed DOWN at the run-up point, holding a coloured PointLight - so
/// the lights point down at the approach (short shadows on the run-up) rather than raking across the lane.
/// Each PointLight casts REAL engine shadows, so the ball + Terry throw four genuine, grounded coloured
/// shadows fanning out over the approach (the coloured penumbrae come from the lights overlapping - blocking
/// the red light leaves a cyan-ish shadow, etc.). They're HDR-bright so they compete with the sun/sky ambient.
///
/// Replaces the single overhead GreenApproachLight. The run-up point (LaneBuilder.RunUpWorldPosition) is
/// procedural, so we resolve it at runtime and build once the lane exists. Lives at the scene root.
/// </summary>
[Title( "Approach Light Rig" )]
[Category( "Terryball" )]
[Icon( "wb_iridescent" )]
public sealed class ApproachLightRig : Component
{
	/// <summary>Post height above the surface (units). Tall keeps the light steep = shadows fall on the approach.</summary>
	[Property] public float Height { get; set; } = 700f;

	/// <summary>Corner distance along the lane from the spawn (units).</summary>
	[Property] public float SpanForward { get; set; } = 700f;

	/// <summary>Corner distance across the lane from the spawn (units).</summary>
	[Property] public float SpanSide { get; set; } = 450f;

	/// <summary>Reach of each coloured PointLight (units).</summary>
	[Property] public float LightRadius { get; set; } = 2500f;

	/// <summary>HDR brightness multiplier on each coloured light. Brightness lives in the LightColor magnitude
	/// (there's no separate field), and the coloured shadows only read if these lights compete with the sun +
	/// sky ambient - so push well above 1. Tune to taste against the wash-out.</summary>
	[Property] public float Brightness { get; set; } = 6f;

	/// <summary>Pole cross-section (units).</summary>
	[Property] public float PostThickness { get; set; } = 16f;

	/// <summary>Head/lamp size (units); the head is stretched along its aim so it reads as pointing down.</summary>
	[Property] public float HeadSize { get; set; } = 60f;

	private bool _built;

	// Corner tag, light colour, forward sign (down-lane), side sign (across-lane). R/G/B/W to the four corners.
	private static readonly (string Tag, Color Col, float Fwd, float Side)[] Corners =
	{
		( "R", new Color( 1f, 0.12f, 0.12f ),  1f,  1f ),
		( "G", new Color( 0.12f, 1f, 0.22f ),  1f, -1f ),
		( "B", new Color( 0.18f, 0.32f, 1f ), -1f,  1f ),
		( "W", new Color( 1f, 1f, 1f ),       -1f, -1f ),
	};

	protected override void OnUpdate()
	{
		if ( _built ) return;

		var lane = Scene.GetAllComponents<LaneBuilder>().FirstOrDefault( b => b.IsValid() );
		if ( !lane.IsValid() ) return; // wait for the procedural lane to exist so RunUpWorldPosition is set

		_built = true;
		Build( lane );
	}

	private void Build( LaneBuilder lane )
	{
		var rot = lane.WorldRotation;
		var fwd = rot.Forward; // local +x, down the lane
		var side = rot.Left;   // local +y, across the lane
		var up = Vector3.Up;
		var origin = lane.RunUpWorldPosition;

		var box = Model.Load( "models/dev/box.vmdl" );
		var bs = box.Bounds.Size; // scale the dev box to real dimensions per-axis

		foreach ( var (tag, col, fdir, sdir) in Corners )
		{
			var basePos = origin + fwd * (fdir * SpanForward) + side * (sdir * SpanSide);
			var headPos = basePos + up * Height;

			// Pole: a slim box from the surface up to the head.
			var pole = new GameObject( true, $"LightPost_{tag}" )
			{
				Parent = GameObject,
				Flags = GameObjectFlags.NotSaved,
				WorldPosition = basePos + up * (Height * 0.5f),
				WorldRotation = rot
			};
			var pr = pole.Components.Create<ModelRenderer>();
			pr.Model = box;
			pr.Tint = new Color( 0.16f, 0.16f, 0.18f );
			pole.LocalScale = new Vector3( PostThickness / bs.x, PostThickness / bs.y, Height / bs.z );

			// Head: a box aimed down at the run-up point (stretched along its aim => reads as a downlight can),
			// tinted its colour, carrying the coloured PointLight the shadow post-process reads.
			var aim = (origin - headPos).Normal;
			var head = new GameObject( true, $"LightHead_{tag}" )
			{
				Parent = GameObject,
				Flags = GameObjectFlags.NotSaved,
				WorldPosition = headPos,
				WorldRotation = Rotation.LookAt( aim, up )
			};
			var hr = head.Components.Create<ModelRenderer>();
			hr.Model = box;
			hr.Tint = col;
			head.LocalScale = new Vector3( (HeadSize * 1.7f) / bs.x, HeadSize / bs.y, HeadSize / bs.z );

			var light = head.Components.Create<PointLight>();
			light.LightColor = col * Brightness; // HDR: brightness is baked into the colour magnitude
			light.Radius = LightRadius;
			light.Shadows = true; // REAL engine shadows: the ball + Terry cast genuine, grounded coloured shadows on the approach
		}
	}
}
