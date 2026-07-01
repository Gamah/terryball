using Sandbox;
using Sandbox.Rendering;

namespace Terryball;

/// <summary>
/// Recolours the ball's REAL shadow in screen space (see PLAN.md). This is the SPIKE stage: it runs a
/// fullscreen pass that red-tints any pixel the engine's directional shadow term reports as shadowed,
/// purely to confirm the shadow bindings are available in a post-process blit. Once that's proven we
/// layer in per-light iteration, the analytic ball mask, and the per-ray galaxy-texture sample.
///
/// Attach to any camera's GameObject (BasePostProcess is auto-collected per camera). Runs after opaque
/// so the sun shadow-map / light constants are most likely still bound.
/// </summary>
[Title( "Ball Shadow Tint" )]
[Category( "Terryball" )]
[Icon( "blur_on" )]
public sealed class BallShadowTintPP : BasePostProcess
{
	/// <summary>SPIKE debug tint. Defaults to red so a working pass is obvious in-editor.</summary>
	[Property] public Color DebugTint { get; set; } = Color.Red;

	/// <summary>Overall recolour strength (0 = off).</summary>
	[Property, Range( 0f, 1f )] public float Strength { get; set; } = 1f;

	private static readonly Material Shader = Material.FromShader( "shaders/ballshadow_tint.shader" );

	public override void Render()
	{
		if ( Strength <= 0f || !Shader.IsValid() )
			return;

		Attributes.Set( "BallShadowDebugTint", new Vector3( DebugTint.r, DebugTint.g, DebugTint.b ) );
		Attributes.Set( "BallShadowStrength", Strength );

		// AfterOpaque: shadow-map + light constants are still bound here (post-process stages later may not be).
		var blit = BlitMode.WithBackbuffer( Shader, Stage.AfterOpaque, 1 );
		Blit( blit, "BallShadowTint" );
	}
}
