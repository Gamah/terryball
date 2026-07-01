using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// A second, static playing ball parked beside the run-up - a different-coloured pokeball (teal/magenta) so
/// its colour-projected shadow reads clearly distinct from the main ball's (red/blue), and the two shadows
/// mix subtractively where they overlap (see BallShadowTintPP). Purely visual: no physics, no occupant, so
/// its shadow shows the pokeball pattern rather than a black Terry silhouette.
///
/// The run-up point (LaneBuilder.RunUpWorldPosition) is procedural, so we resolve it at runtime and build
/// once the lane exists. Lives at the scene root.
/// </summary>
[Title( "Shadow Test Ball" )]
[Category( "Terryball" )]
[Icon( "sports_baseball" )]
public sealed class ShadowTestBall : Component
{
	/// <summary>Ball radius / shadow volume (units). Matches the main ball by default.</summary>
	[Property] public float Radius { get; set; } = 100f;

	/// <summary>Lateral offset (units) from the run-up centre - which side of the approach the ball sits.</summary>
	[Property] public float SideOffset { get; set; } = 300f;

	/// <summary>Forward offset (units) along the lane from the run-up centre.</summary>
	[Property] public float ForwardOffset { get; set; } = 0f;

	private bool _built;

	protected override void OnUpdate()
	{
		if ( _built ) return;

		var lane = Scene.GetAllComponents<LaneBuilder>().FirstOrDefault( b => b.IsValid() );
		if ( !lane.IsValid() ) return; // wait for the procedural lane so RunUpWorldPosition is set

		_built = true;
		Build( lane );
	}

	private void Build( LaneBuilder lane )
	{
		var rot = lane.WorldRotation;
		var origin = lane.RunUpWorldPosition;
		var pos = origin + rot.Left * SideOffset + rot.Forward * ForwardOffset + Vector3.Up * Radius;

		var ball = new GameObject( true, "ShadowTestBall(visual)" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved,
			WorldPosition = pos
		};

		var shell = ball.Components.Create<ModelRenderer>();
		shell.Model = Model.Load( "models/dev/sphere.vmdl" );
		shell.MaterialOverride = Material.Load( "materials/glassball_poke_teal.vmat" );
		var shellBounds = shell.Model.Bounds.Size.x; // dev sphere ~64 across
		ball.LocalScale = new Vector3( (Radius * 2f) / shellBounds );

		// Cast a colour-projected shadow off the teal/magenta map (same map the shell renders, so they match).
		var sb = ball.Components.Create<ShadowBall>();
		sb.Radius = Radius;
		sb.ColorMapPath = "materials/pokeball_teal_color.png";
		sb.HasOpaqueOccupant = false;
	}
}
