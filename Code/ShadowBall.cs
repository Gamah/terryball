using Sandbox;

namespace Terryball;

/// <summary>
/// Marks a GameObject as a ball that casts a colour-projected shadow (see BallShadowTintPP / PLAN.md). The
/// post-process gathers every ShadowBall and projects its <see cref="ColorMap"/> into its real shadow,
/// sampled per-ray - so a two-tone ball (pokeball) casts a two-tone shadow, and different balls cast
/// different-coloured shadows. Overlapping shadows mix subtractively.
///
/// Reads the GameObject's world transform, so the shadow follows the ball as it moves/rolls.
/// </summary>
[Title( "Shadow Ball" )]
[Category( "Terryball" )]
[Icon( "brightness_3" )]
public sealed class ShadowBall : Component
{
	/// <summary>World radius of the ball's shadow volume (units).</summary>
	[Property] public float Radius { get; set; } = 100f;

	/// <summary>The ball's colour map (equirectangular), projected into the shadow per-ray. Null = flat white
	/// (so <see cref="Tint"/> alone gives a flat-coloured shadow). Ideally the same map the ball renders.</summary>
	[Property] public Texture ColorMap { get; set; }

	/// <summary>Path to load <see cref="ColorMap"/> from if it isn't set directly (resolved in the post-process,
	/// which runs after asset load - a runtime Texture.Load during OnEnabled can come back null).</summary>
	[Property] public string ColorMapPath { get; set; } = "";

	/// <summary>Multiplier over the sampled colour map (leave white to use the map as-is).</summary>
	[Property] public Color Tint { get; set; } = Color.White;

	/// <summary>True if an opaque occupant rides inside (Terry in the glass ball) - its silhouette reads black
	/// in the shadow instead of taking the colour. Leave false for plain colour balls.</summary>
	[Property] public bool HasOpaqueOccupant { get; set; }
}
