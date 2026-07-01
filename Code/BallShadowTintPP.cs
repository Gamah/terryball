using Sandbox;
using Sandbox.Rendering;
using System.Linq;

namespace Terryball;

/// <summary>
/// Recolours the ball's REAL shadow in screen space (see PLAN.md). A fullscreen pass runs after opaque,
/// reconstructs each pixel's world position from depth, reads the engine's real directional-shadow term
/// (preserving its grading + Terry silhouette), masks to the ball's analytic shadow volume, and tints it
/// with the exact galaxy texel the sun ray passed through — so the shadow reads as the ball's own colour.
///
/// Single-ball / single-sun stage. Per-light iteration and multi-ball arrays are the next steps.
/// Attach to any camera's GameObject (BasePostProcess is auto-collected per camera).
/// </summary>
[Title( "Ball Shadow Tint" )]
[Category( "Terryball" )]
[Icon( "blur_on" )]
public sealed class BallShadowTintPP : BasePostProcess
{
	/// <summary>Overall recolour strength (0 = off).</summary>
	[Property, Range( 0f, 1f )] public float Strength { get; set; } = 1f;

	/// <summary>Static tint lift on top of the sampled galaxy colour (matches the vmat's g_vColorTint).</summary>
	[Property] public Color GlassTint { get; set; } = new Color( 0.82f, 1f, 0.88f );

	/// <summary>Ball colour texture, sampled per-ray. Defaults to the glass ball's galaxy map.</summary>
	[Property] public string GalaxyTexturePath { get; set; } = "materials/galaxyball_color.png";

	/// <summary>Debug tint for the diagnostic modes (unused by Final).</summary>
	[Property] public Color DebugTint { get; set; } = Color.Red;

	/// <summary>
	/// Diagnostic view. Final is the real effect; the rest isolate the pipeline:
	/// 1 whole-screen tint, 2 depth greyscale, 3 shadow-cbuffer bound?, 4 all shadows red,
	/// 5 ball-shadow mask, 6 the real recolour.
	/// </summary>
	public enum DebugModes { Full = 1, Depth = 2, CascadesBound = 3, AllShadows = 4, BallMask = 5, Final = 6 }

	[Property] public DebugModes DebugMode { get; set; } = DebugModes.Final;

	private static readonly Material Shader = Material.FromShader( "shaders/ballshadow_tint.shader" );

	private Texture _galaxy;
	private string _galaxyLoadedPath;

	public override void Render()
	{
		if ( Strength <= 0f || !Shader.IsValid() )
			return;

		// Cache the galaxy texture; reload only if the path changes.
		if ( _galaxy is null || _galaxyLoadedPath != GalaxyTexturePath )
		{
			_galaxy = Texture.Load( GalaxyTexturePath );
			_galaxyLoadedPath = GalaxyTexturePath;
		}

		// Find a ball to cast a tinted shadow. Single-ball stage: the first enabled one drives it.
		var ball = Scene.GetAllComponents<BallController>().FirstOrDefault( b => b.IsValid() && b.Active );

		Attributes.Set( "BallShadowStrength", Strength );
		Attributes.Set( "BallGlassTint", new Vector3( GlassTint.r, GlassTint.g, GlassTint.b ) );
		Attributes.Set( "BallShadowDebugTint", new Vector3( DebugTint.r, DebugTint.g, DebugTint.b ) );
		Attributes.Set( "BallShadowDebugMode", (int)DebugMode );
		if ( _galaxy is not null )
			Attributes.Set( "BallGalaxyTex", _galaxy );

		if ( ball is not null )
		{
			var rot = ball.WorldRotation;
			Attributes.Set( "BallShadowCenter", ball.WorldPosition );
			Attributes.Set( "BallShadowRadius", ball.Radius );
			Attributes.Set( "BallShadowRot", new Vector4( rot.x, rot.y, rot.z, rot.w ) );
		}
		else
		{
			Attributes.Set( "BallShadowRadius", 0f ); // shader bails
		}

		// AfterOpaque: shadow-map + light constants are still bound here.
		var blit = BlitMode.WithBackbuffer( Shader, Stage.AfterOpaque, 1 );
		Blit( blit, "BallShadowTint" );
	}
}
