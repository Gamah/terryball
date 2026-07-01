using Sandbox;

namespace Terryball;

/// <summary>
/// Marks a GameObject as a ball that casts a coloured shadow (see BallShadowTintPP / PLAN.md). The
/// post-process gathers every ShadowBall and recolours its real shadow by <see cref="Tint"/> (optionally
/// modulated by the shared galaxy texture, per-ray). Put one on the hamster ball; put more (different
/// tints) around the lane and their shadows mix subtractively where they overlap.
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

	/// <summary>The colour the shadow is tinted toward (the glass colour). Defaults to the galaxy green.</summary>
	[Property] public Color Tint { get; set; } = new Color( 0.82f, 1f, 0.88f );

	/// <summary>Modulate the tint by the per-ray galaxy texel (texture detail) vs a flat tint.</summary>
	[Property] public bool UseGalaxyTexture { get; set; } = true;
}
