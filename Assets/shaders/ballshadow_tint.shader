//
// Ball shadow tint - recolours each ball's REAL shadow in screen space (see PLAN.md).
//
// Reconstruct world pos from depth, read the engine's real directional-shadow term (keeps its grading +
// Terry silhouette), mask to each ball's analytic shadow volume, and tint by the galaxy texel the sun ray
// passed through. Multiple balls stack SUBTRACTIVELY (each occluder filters the light), so overlapping
// coloured shadows mix (green * blue -> dark). Opaque occupants (Terry) read black, not tinted.
//
// ASCII ONLY and keep every section brace balanced - the vfx source parser crashes on load otherwise.
//
HEADER
{
    Description = "Ball shadow tint (per-ray galaxy, multi-ball subtractive)";
    DevShader = true;
}

MODES
{
    Default();
    Forward();
}

FEATURES
{
}

COMMON
{
    #include "postprocess/shared.hlsl"
}

struct VertexInput
{
    float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
    float2 vTexCoord : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};

struct PixelInput
{
    float2 uv : TEXCOORD0;

    #if ( PROGRAM == VFX_PROGRAM_VS )
        float4 vPositionPs : SV_Position;
    #endif

    #if ( PROGRAM == VFX_PROGRAM_PS )
        float4 vPositionSs : SV_Position;
    #endif
};

VS
{
    PixelInput MainVs( VertexInput i )
    {
        PixelInput o;
        o.vPositionPs = float4( i.vPositionOs.xy, 0.0f, 1.0f );
        o.uv = i.vTexCoord;
        return o;
    }
}

PS
{
    #include "postprocess/common.hlsl"
    #include "common/classes/Depth.hlsl"
    #include "common/Shadow.hlsl"

    Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;

    // The balls' colour texture (galaxy nebula) - sampled per-ray for the shadow tint.
    Texture2D g_tGalaxy < Attribute( "BallGalaxyTex" ); SrgbRead( true ); >;
    SamplerState g_sGalaxy < Filter( BILINEAR ); AddressU( WRAP ); AddressV( CLAMP ); >;

    float  g_flStrength     < Attribute( "BallShadowStrength" ); Default( 1.0f ); >;
    float  g_flTerryCut     < Attribute( "BallShadowTerryCut" ); Default( 0.92f ); >; // occlusion above this = opaque -> black
    float  g_flGalaxyDetail < Attribute( "BallShadowGalaxyDetail" ); Default( 0.4f ); >; // 0 = flat tint, 1 = full swirl luminance
    float  g_flColorize     < Attribute( "BallShadowColorize" ); Default( 1.0f ); >; // 0 = multiply (physical), 1 = colorize (assertive)
    float3 g_vTintColor     < Attribute( "BallShadowDebugTint" ); Default3( 1.0f, 0.0f, 0.0f ); >; // debug modes only

    // Up to 4 balls. Per slot: A = (center.xyz, radius); Tint = (rgb, useGalaxy); Rot = world quat.
    // radius <= 0 => empty slot.
    float4 g_vBallA0 < Attribute( "ShadowBallA0" ); Default4( 0, 0, 0, 0 ); >;
    float4 g_vBallA1 < Attribute( "ShadowBallA1" ); Default4( 0, 0, 0, 0 ); >;
    float4 g_vBallA2 < Attribute( "ShadowBallA2" ); Default4( 0, 0, 0, 0 ); >;
    float4 g_vBallA3 < Attribute( "ShadowBallA3" ); Default4( 0, 0, 0, 0 ); >;
    float4 g_vBallT0 < Attribute( "ShadowBallT0" ); Default4( 1, 1, 1, 1 ); >;
    float4 g_vBallT1 < Attribute( "ShadowBallT1" ); Default4( 1, 1, 1, 1 ); >;
    float4 g_vBallT2 < Attribute( "ShadowBallT2" ); Default4( 1, 1, 1, 1 ); >;
    float4 g_vBallT3 < Attribute( "ShadowBallT3" ); Default4( 1, 1, 1, 1 ); >;
    float4 g_vBallR0 < Attribute( "ShadowBallR0" ); Default4( 0, 0, 0, 1 ); >;
    float4 g_vBallR1 < Attribute( "ShadowBallR1" ); Default4( 0, 0, 0, 1 ); >;
    float4 g_vBallR2 < Attribute( "ShadowBallR2" ); Default4( 0, 0, 0, 1 ); >;
    float4 g_vBallR3 < Attribute( "ShadowBallR3" ); Default4( 0, 0, 0, 1 ); >;

    // DIAGNOSTIC MODE (live-switchable uniform - flip in the inspector, no recompile):
    //   1 FULL       : tint the WHOLE screen (pass runs + blit writes back?)
    //   2 DEPTH      : normalized depth greyscale (depth chain bound?)
    //   3 CASCADES   : GREEN if the directional-shadow cbuffer is bound, else RED
    //   4 ALLSHADOWS : red-tint EVERY shadow (the original spike)
    //   5 BALLMASK   : white where any ball's analytic shadow volume covers this pixel
    //   6 FINAL      : the real effect - ball shadows recoloured, mixed subtractively
    int g_nDebugMode < Attribute( "BallShadowDebugMode" ); Default( 6 ); >;

    // Rotate vector v by quaternion q (xyz,w).
    float3 QuatRotate( float4 q, float3 v )
    {
        return v + 2.0f * cross( q.xyz, cross( q.xyz, v ) + q.w * v );
    }

    // Analytic soft mask: is worldPos inside this ball's shadow volume for a light in direction vToLight?
    // Returns 0..1 (soft rim) and outputs the light-facing surface hit point on the ball.
    float BallShadowMask( float3 vWorldPos, float3 vToLight, float3 center, float radius, out float3 vHit )
    {
        float3 oc = vWorldPos - center;
        float b = dot( oc, vToLight );                 // closest-approach param is -b
        float perp2 = max( dot( oc, oc ) - b * b, 0.0f );
        float d = sqrt( perp2 );

        // The far intersection (toward the light) - the surface the light enters.
        float c = dot( oc, oc ) - radius * radius;
        float disc = b * b - c;
        float tFar = -b + sqrt( max( disc, 0.0f ) );
        vHit = vWorldPos + vToLight * tFar;

        // Solid across the whole disc with only a thin AA edge, so the dense rim (darkest green) is still
        // tinted - a wide fade here "culls" the edge to gray, the opposite of what we want. Gated to "the
        // ball is toward the light" (closest approach ahead).
        float ahead = step( 0.0f, -b );
        float mask = 1.0f - smoothstep( radius * 0.97f, radius * 1.02f, d );
        return mask * ahead;
    }

    // Sample the galaxy texel the light ray passed through: world hit -> ball-local -> spherical UV.
    float3 SampleGalaxy( float3 vHit, float3 center, float4 rot )
    {
        // Inverse-rotate the world hit normal into ball-local space, then spherical UV.
        // Constants: 0.15915494 = 1 over two pi, 0.31830989 = 1 over pi.
        float3 n = normalize( vHit - center );
        float3 nl = normalize( QuatRotate( float4( -rot.xyz, rot.w ), n ) );
        float u = atan2( nl.y, nl.x ) * 0.15915494f + 0.5f;
        float v = acos( clamp( nl.z, -1.0f, 1.0f ) ) * 0.31830989f;
        // Sample a blurred mip: a fullscreen pass has no UV gradient so mip 0 prints the texture's grain.
        return g_tGalaxy.SampleLevel( g_sGalaxy, float2( u, v ), 2.0f ).rgb;
    }

    // Stack one ball's tint into the subtractive filter and grow the union mask.
    void AccumBall( float3 wp, float3 toL, float4 a, float4 tint, float4 rot, inout float3 filter, inout float mask )
    {
        float radius = a.w;
        if ( radius <= 0.0f )
            return;

        float3 hit;
        float m = BallShadowMask( wp, toL, a.xyz, radius, hit );
        if ( m <= 0.0f )
            return;

        float3 col = tint.rgb;                          // HUE comes from the ball's own colour
        if ( tint.w > 0.5f )                            // useGalaxy: modulate only the BRIGHTNESS by the swirl (keeps hue)
        {
            float3 g = SampleGalaxy( hit, a.xyz, rot );
            float gl = dot( g, float3( 0.299f, 0.587f, 0.114f ) );
            col *= lerp( 1.0f, saturate( gl * 1.6f ), g_flGalaxyDetail );
        }

        filter *= lerp( float3( 1, 1, 1 ), col, m );    // subtractive: overlapping balls multiply
        mask = max( mask, m );
    }

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float2 vScreenUv = CalculateViewportUv( i.vPositionSs.xy );
        float4 color = g_tColorBuffer.Sample( g_sBilinearMirror, vScreenUv );

        if ( g_nDebugMode == 1 )
        {
            color.rgb = lerp( color.rgb, g_vTintColor, g_flStrength );
            return color;
        }

        if ( g_nDebugMode == 2 )
        {
            float dd = Depth::GetNormalized( i.vPositionSs.xy );
            return float4( dd.xxx, 1.0f );
        }

        if ( g_nDebugMode == 3 )
        {
            float3 cc = ( g_DirectionalLightCascadeCount > 0 ) ? float3( 0, 1, 0 ) : float3( 1, 0, 0 );
            return float4( cc, 1.0f );
        }

        float3 vWorldPos = Depth::GetWorldPosition( i.vPositionSs.xy );
        float flVisibility = DirectionalLightShadow::GetVisibility( vWorldPos, i.vPositionSs.xy );
        float flShadow = saturate( 1.0f - flVisibility );

        if ( g_nDebugMode == 4 )
        {
            color.rgb = lerp( color.rgb, g_vTintColor, flShadow * g_flStrength );
            return color;
        }

        // Direction from this surface point toward the sun (light travels along g_DirectionalLightDirection).
        float3 vToSun = normalize( -g_DirectionalLightDirection.xyz );

        // Accumulate every ball's tint subtractively; union their masks.
        float3 filter = float3( 1, 1, 1 );
        float mask = 0.0f;
        AccumBall( vWorldPos, vToSun, g_vBallA0, g_vBallT0, g_vBallR0, filter, mask );
        AccumBall( vWorldPos, vToSun, g_vBallA1, g_vBallT1, g_vBallR1, filter, mask );
        AccumBall( vWorldPos, vToSun, g_vBallA2, g_vBallT2, g_vBallR2, filter, mask );
        AccumBall( vWorldPos, vToSun, g_vBallA3, g_vBallT3, g_vBallR3, filter, mask );

        if ( g_nDebugMode == 5 )
            return float4( mask.xxx, 1.0f );

        // Opaque occupants (Terry) render solid in the shadow map (flShadow ~1) while dithered glass stays
        // fractional - so where occlusion saturates past the cut, drive the tint to black (all light blocked).
        float opaque = smoothstep( g_flTerryCut, 1.0f, flShadow );
        filter = lerp( filter, float3( 0, 0, 0 ), opaque );

        // FINAL: paint the ball's hue across its WHOLE shadow footprint (mask) - NOT gated by flShadow, so
        // clear glass still reads green. The gradient comes for free from the already-shadowed scene
        // brightness (baseLuma): dark at the dense rim -> dark green, light at the clear centre -> light
        // green, Terry (filter forced to 0) -> black. Colorize so it reads as colour, not a gray print.
        float baseLuma = dot( color.rgb, float3( 0.299f, 0.587f, 0.114f ) );
        float3 tinted = lerp( color.rgb * filter, baseLuma * filter, g_flColorize );
        float t = mask * g_flStrength;
        color.rgb = lerp( color.rgb, tinted, t );
        return color;
    }
}
