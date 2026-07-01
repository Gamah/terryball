//
// Ball shadow tint - projects each ball's SURFACE COLOUR into its real shadow (see PLAN.md).
//
// For every shadowed pixel we trace the ray toward the sun, find where it pierces each ball, sample that
// ball's colour map at that surface point (so a pokeball's red half lands red in the shadow, blue half
// blue - the shadow is a spatial projection of the ball, not one flat colour), and colourise the real
// shadow with it. Balls stack SUBTRACTIVELY where their shadows overlap. An opaque occupant (Terry inside
// the glass) reads black. Per-ball colour maps => different balls cast different-coloured shadows.
//
// ASCII ONLY and keep every section brace balanced - the vfx source parser crashes on load otherwise.
//
HEADER
{
    Description = "Ball shadow tint (per-ray surface-colour projection, multi-ball subtractive)";
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

    // Per-ball colour maps (equirectangular). The ball's shadow is a per-ray projection of these.
    Texture2D g_tBall0 < Attribute( "ShadowBallTex0" ); SrgbRead( true ); >;
    Texture2D g_tBall1 < Attribute( "ShadowBallTex1" ); SrgbRead( true ); >;
    Texture2D g_tBall2 < Attribute( "ShadowBallTex2" ); SrgbRead( true ); >;
    Texture2D g_tBall3 < Attribute( "ShadowBallTex3" ); SrgbRead( true ); >;
    SamplerState g_sBall < Filter( BILINEAR ); AddressU( WRAP ); AddressV( CLAMP ); >;

    float g_flStrength < Attribute( "BallShadowStrength" ); Default( 1.0f ); >;
    float g_flTerryCut < Attribute( "BallShadowTerryCut" ); Default( 0.92f ); >; // occlusion above this, under an occupant ball, = black
    float g_flColorize < Attribute( "BallShadowColorize" ); Default( 1.0f ); >;  // 0 = multiply (physical), 1 = colorize (assertive)
    float g_flColorMip < Attribute( "BallShadowColorMip" ); Default( 0.0f ); >;  // >0 blurs the colour map (kills grain)
    float3 g_vTintColor < Attribute( "BallShadowDebugTint" ); Default3( 1.0f, 0.0f, 0.0f ); >; // debug modes only

    // Up to 4 balls. A = (center.xyz, radius); T = (tint.rgb, hasOpaqueOccupant); R = world quat.
    // radius <= 0 => empty slot.
    float4 g_vBallA0 < Attribute( "ShadowBallA0" ); Default4( 0, 0, 0, 0 ); >;
    float4 g_vBallA1 < Attribute( "ShadowBallA1" ); Default4( 0, 0, 0, 0 ); >;
    float4 g_vBallA2 < Attribute( "ShadowBallA2" ); Default4( 0, 0, 0, 0 ); >;
    float4 g_vBallA3 < Attribute( "ShadowBallA3" ); Default4( 0, 0, 0, 0 ); >;
    float4 g_vBallT0 < Attribute( "ShadowBallT0" ); Default4( 1, 1, 1, 0 ); >;
    float4 g_vBallT1 < Attribute( "ShadowBallT1" ); Default4( 1, 1, 1, 0 ); >;
    float4 g_vBallT2 < Attribute( "ShadowBallT2" ); Default4( 1, 1, 1, 0 ); >;
    float4 g_vBallT3 < Attribute( "ShadowBallT3" ); Default4( 1, 1, 1, 0 ); >;
    float4 g_vBallR0 < Attribute( "ShadowBallR0" ); Default4( 0, 0, 0, 1 ); >;
    float4 g_vBallR1 < Attribute( "ShadowBallR1" ); Default4( 0, 0, 0, 1 ); >;
    float4 g_vBallR2 < Attribute( "ShadowBallR2" ); Default4( 0, 0, 0, 1 ); >;
    float4 g_vBallR3 < Attribute( "ShadowBallR3" ); Default4( 0, 0, 0, 1 ); >;

    // DIAGNOSTIC MODE (live-switchable uniform - flip in the inspector, no recompile):
    //   1 FULL / 2 DEPTH / 3 CASCADES / 4 ALLSHADOWS / 5 BALLMASK / 6 FINAL (the real effect)
    int g_nDebugMode < Attribute( "BallShadowDebugMode" ); Default( 6 ); >;

    float3 QuatRotate( float4 q, float3 v )
    {
        return v + 2.0f * cross( q.xyz, cross( q.xyz, v ) + q.w * v );
    }

    // Analytic soft mask: is worldPos in this ball's shadow volume for a light in direction vToLight?
    // 0..1 (thin AA edge) and outputs the light-facing surface hit point on the ball.
    float BallShadowMask( float3 vWorldPos, float3 vToLight, float3 center, float radius, out float3 vHit )
    {
        float3 oc = vWorldPos - center;
        float b = dot( oc, vToLight );
        float perp2 = max( dot( oc, oc ) - b * b, 0.0f );
        float d = sqrt( perp2 );

        float c = dot( oc, oc ) - radius * radius;
        float disc = b * b - c;
        float tFar = -b + sqrt( max( disc, 0.0f ) ); // far intersection = surface the light enters
        vHit = vWorldPos + vToLight * tFar;

        float ahead = step( 0.0f, -b ); // ball is toward the light
        float mask = 1.0f - smoothstep( radius * 0.97f, radius * 1.02f, d );
        return mask * ahead;
    }

    // Ball surface colour at the light-facing hit: world hit -> ball-local -> spherical UV -> map.
    float3 SampleBall( Texture2D tex, float3 vHit, float3 center, float4 rot )
    {
        float3 n = normalize( vHit - center );
        float3 nl = normalize( QuatRotate( float4( -rot.xyz, rot.w ), n ) ); // inverse-rotate to ball-local
        float u = atan2( nl.y, nl.x ) * 0.15915494f + 0.5f; // 1/(2pi)
        float v = acos( clamp( nl.z, -1.0f, 1.0f ) ) * 0.31830989f; // 1/pi
        return tex.SampleLevel( g_sBall, float2( u, v ), g_flColorMip ).rgb;
    }

    // Stack one ball into the subtractive filter, grow the union mask, and (if it holds an opaque occupant)
    // the occupant mask used for the Terry-black cut.
    void AccumBall( Texture2D tex, float3 wp, float3 toL, float4 a, float4 t, float4 rot,
                    inout float3 filter, inout float mask, inout float occ )
    {
        float radius = a.w;
        if ( radius <= 0.0f )
            return;

        float3 hit;
        float m = BallShadowMask( wp, toL, a.xyz, radius, hit );
        if ( m <= 0.0f )
            return;

        float3 col = SampleBall( tex, hit, a.xyz, rot ) * t.rgb; // surface colour x optional tint
        filter *= lerp( float3( 1, 1, 1 ), col, m );             // subtractive: overlapping balls multiply
        mask = max( mask, m );
        if ( t.w > 0.5f )
            occ = max( occ, m );
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

        // Direction from this surface point toward the sun.
        float3 vToSun = normalize( -g_DirectionalLightDirection.xyz );

        // Project every ball's surface colour, stacking subtractively; union the masks.
        float3 filter = float3( 1, 1, 1 );
        float mask = 0.0f;
        float occ = 0.0f;
        AccumBall( g_tBall0, vWorldPos, vToSun, g_vBallA0, g_vBallT0, g_vBallR0, filter, mask, occ );
        AccumBall( g_tBall1, vWorldPos, vToSun, g_vBallA1, g_vBallT1, g_vBallR1, filter, mask, occ );
        AccumBall( g_tBall2, vWorldPos, vToSun, g_vBallA2, g_vBallT2, g_vBallR2, filter, mask, occ );
        AccumBall( g_tBall3, vWorldPos, vToSun, g_vBallA3, g_vBallT3, g_vBallR3, filter, mask, occ );

        if ( g_nDebugMode == 5 )
            return float4( mask.xxx, 1.0f );

        // Opaque occupant (Terry) inside an occupant ball reads black: where THAT ball's mask overlaps a
        // saturated real shadow, force the tint to black. Colour balls (no occupant) keep their colour.
        float opaque = smoothstep( g_flTerryCut, 1.0f, flShadow ) * occ;
        filter = lerp( filter, float3( 0, 0, 0 ), opaque );

        // Paint the projected colour across the whole footprint; the already-shadowed brightness (baseLuma)
        // gives the light/dark gradient. Colorize so the hue reads instead of a gray print.
        float baseLuma = dot( color.rgb, float3( 0.299f, 0.587f, 0.114f ) );
        float3 tinted = lerp( color.rgb * filter, baseLuma * filter, g_flColorize );
        color.rgb = lerp( color.rgb, tinted, mask * g_flStrength );
        return color;
    }
}
