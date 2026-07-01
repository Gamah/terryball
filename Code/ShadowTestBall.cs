using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// TEST PROP: spawns a static, differently-coloured glass ball beside the run-up so its shadow can be
/// seen mixing with the hamster ball's (see BallShadowTintPP). Uses the same translucent glass material
/// so it casts the same fractional shadow (an opaque ball would read black under the Terry-cut), and a
/// <see cref="ShadowBall"/> with a contrasting <see cref="Tint"/> so overlaps mix subtractively.
/// Remove this component (and its scene object) once multi-ball shadows are dialled in.
/// </summary>
[Title( "Shadow Test Ball" )]
[Category( "Terryball" )]
[Icon( "sports_baseball" )]
public sealed class ShadowTestBall : Component
{
	[Property] public float Radius { get; set; } = 100f;
	/// <summary>Shadow tint - blue by default so green + blue overlap reads clearly dark (subtractive).</summary>
	[Property] public Color Tint { get; set; } = new Color( 0.30f, 0.45f, 1f );
	/// <summary>Lateral offset (units) from the run-up centre; ~1.2 diameters so shadows can overlap at an angle.</summary>
	[Property] public float SideOffset { get; set; } = 240f;
	/// <summary>Forward offset (units) along the lane from the run-up centre.</summary>
	[Property] public float ForwardOffset { get; set; } = 0f;

	private GameObject _ball;

	protected override void OnStart()
	{
		var lane = Scene.GetAllComponents<LaneBuilder>().FirstOrDefault( b => b.IsValid() );
		var runUp = lane.IsValid() ? lane.RunUpWorldPosition : Vector3.Zero;

		// Beside the run-up, sitting on the surface (run-up point is on the platform top).
		var pos = runUp + Vector3.Left * SideOffset + Vector3.Forward * ForwardOffset + Vector3.Up * Radius;

		_ball = new GameObject( true, "ShadowTestBall(visual)" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved,
			WorldPosition = pos
		};

		var shell = _ball.Components.Create<ModelRenderer>();
		shell.Model = Model.Load( "models/dev/sphere.vmdl" );
		shell.MaterialOverride = Material.Load( "materials/glassball.vmat" );
		var shellBounds = shell.Model.Bounds.Size.x; // dev sphere ~64 across
		_ball.LocalScale = new Vector3( (Radius * 2f) / shellBounds );

		var sb = _ball.Components.Create<ShadowBall>();
		sb.Radius = Radius;
		sb.Tint = Tint;
		sb.UseGalaxyTexture = true;
	}
}
