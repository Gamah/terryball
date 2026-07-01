using Sandbox;
using Sandbox.Rendering;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Projects each ball's surface colour into its REAL shadow, in screen space (see PLAN.md). A fullscreen
/// pass runs after opaque, reconstructs each pixel's world position from depth, reads the engine's real
/// directional-shadow term, and for every ShadowBall samples its colour map at the point the sun ray
/// pierced the ball - so the shadow is a spatial projection of the ball (a pokeball casts a two-tone
/// shadow). Balls mix subtractively where their shadows overlap; opaque occupants (Terry) read black.
///
/// Attach to any camera's GameObject (BasePostProcess is auto-collected per camera).
/// </summary>
[Title( "Ball Shadow Tint" )]
[Category( "Terryball" )]
[Icon( "blur_on" )]
public sealed class BallShadowTintPP : BasePostProcess
{
	/// <summary>Overall recolour strength (0 = off).</summary>
	[Property, Range( 0f, 1f )] public float Strength { get; set; } = 1f;

	/// <summary>Under an occupant ball, occlusion above this reads as the opaque occupant (Terry) and goes
	/// black instead of taking the colour. High so only the solid silhouette darkens, not the dithered glass.</summary>
	[Property, Range( 0.5f, 1f )] public float TerryCut { get; set; } = 0.92f;

	/// <summary>0 = multiply (dark, shadow-like), 1 = colorize (bright/vibrant). Lower reads more like a shadow.</summary>
	[Property, Range( 0f, 1f )] public float Colorize { get; set; } = 0.5f;

	/// <summary>Projected-colour saturation. 1 = full colour (neon), 0 = greyscale shadow. Lower = more muted.</summary>
	[Property, Range( 0f, 1f )] public float Saturation { get; set; } = 0.65f;

	/// <summary>Blur the colour map (mip level) to kill per-pixel grain; 0 = sharp (good for hard splits).</summary>
	[Property, Range( 0f, 6f )] public float ColorMip { get; set; } = 0.5f;

	/// <summary>Debug tint for the diagnostic modes (unused by Final).</summary>
	[Property] public Color DebugTint { get; set; } = Color.Red;

	/// <summary>
	/// Diagnostic view. Final is the real effect; the rest isolate the pipeline:
	/// 1 whole-screen tint, 2 depth greyscale, 3 shadow-cbuffer bound?, 4 all shadows red,
	/// 5 ball-shadow mask, 6 the real projection.
	/// </summary>
	public enum DebugModes { Full = 1, Depth = 2, CascadesBound = 3, AllShadows = 4, BallMask = 5, Final = 6, ProjectedColor = 7 }

	[Property] public DebugModes DebugMode { get; set; } = DebugModes.Final;

	// --- Shadow-map fidelity ------------------------------------------------------------
	// The effect samples the engine's real sun shadow, so its sharpness is the directional cascade texel
	// size. Engine defaults spread 2048-texel cascades over 15000 units, pixelating the ball's small shadow.
	// We pack resolution onto the lane: bigger cascades + a shorter cascade distance (the camera hugs the
	// ball). Global render convars, applied here since this is the shadow feature.

	/// <summary>Per-cascade directional shadow-map resolution (r.shadows.csm.maxresolution). Higher = sharper, more VRAM.</summary>
	[Property, Group( "Shadow Quality" ), Range( 512, 8192 )] public int CascadeResolution { get; set; } = 4096;

	/// <summary>Max distance the sun's cascades cover (r.shadows.csm.distance). Lower packs texels near the camera/ball = sharper.</summary>
	[Property, Group( "Shadow Quality" ), Range( 1500, 15000 )] public float CascadeDistance { get; set; } = 6000f;

	/// <summary>Max balls the shader handles (unrolled slots). Keep in sync with the shader.</summary>
	private const int MaxBalls = 4;

	private static readonly Material Shader = Material.FromShader( "shaders/ballshadow_tint.shader" );

	private int _appliedRes = -1;
	private float _appliedDist = -1f;

	// Resolve colour maps by path in the post-process (runs after asset load, unlike OnEnabled), cached.
	private static readonly Dictionary<string, Texture> _mapCache = new();
	private static Texture ResolveMap( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return null;
		if ( !_mapCache.TryGetValue( path, out var t ) )
		{
			t = Texture.Load( path );
			_mapCache[path] = t;
		}
		return t;
	}

	// Push the shadow-quality convars, but only when they actually change (SetValue every frame is wasteful).
	private void ApplyShadowQuality()
	{
		if ( _appliedRes != CascadeResolution )
		{
			ConsoleSystem.SetValue( "r.shadows.csm.maxresolution", CascadeResolution );
			_appliedRes = CascadeResolution;
		}
		if ( _appliedDist != CascadeDistance )
		{
			ConsoleSystem.SetValue( "r.shadows.csm.distance", CascadeDistance );
			_appliedDist = CascadeDistance;
		}
	}

	public override void Render()
	{
		if ( Strength <= 0f || !Shader.IsValid() )
			return;

		ApplyShadowQuality();

		Attributes.Set( "BallShadowStrength", Strength );
		Attributes.Set( "BallShadowTerryCut", TerryCut );
		Attributes.Set( "BallShadowColorize", Colorize );
		Attributes.Set( "BallShadowSaturation", Saturation );
		Attributes.Set( "BallShadowColorMip", ColorMip );
		Attributes.Set( "BallShadowDebugTint", new Vector3( DebugTint.r, DebugTint.g, DebugTint.b ) );
		Attributes.Set( "BallShadowDebugMode", (int)DebugMode );

		// Gather every ShadowBall and fill the shader's unrolled slots (empty slots => radius 0 => skipped).
		var balls = Scene.GetAllComponents<ShadowBall>().Where( b => b.IsValid() && b.Active ).Take( MaxBalls ).ToList();
		for ( int i = 0; i < MaxBalls; i++ )
		{
			if ( i < balls.Count )
			{
				var b = balls[i];
				var p = b.WorldPosition;
				var rot = b.WorldRotation;
				Attributes.Set( $"ShadowBallA{i}", new Vector4( p.x, p.y, p.z, b.Radius ) );
				Attributes.Set( $"ShadowBallT{i}", new Vector4( b.Tint.r, b.Tint.g, b.Tint.b, b.HasOpaqueOccupant ? 1f : 0f ) );
				Attributes.Set( $"ShadowBallR{i}", new Vector4( rot.x, rot.y, rot.z, rot.w ) );
				Attributes.Set( $"ShadowBallTex{i}", b.ColorMap ?? ResolveMap( b.ColorMapPath ) ?? Texture.White ); // null => flat (Tint)
			}
			else
			{
				Attributes.Set( $"ShadowBallA{i}", new Vector4( 0f, 0f, 0f, 0f ) ); // empty slot
			}
		}

		// AfterOpaque: shadow-map + light constants are still bound here.
		var blit = BlitMode.WithBackbuffer( Shader, Stage.AfterOpaque, 1 );
		Blit( blit, "BallShadowTint" );
	}
}
