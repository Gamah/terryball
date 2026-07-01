//
// Ball shadow tint - SPIKE STAGE.
//
// Goal of this stage (see PLAN.md "De-risk spike"): prove that the engine's real directional-shadow
// term is readable from a fullscreen post-process blit. We reconstruct world position from depth and
// call DirectionalLightShadow::GetVisibility; wherever the pixel is in shadow we tint it RED.
//
//   red on the lane's shadow in-editor  => bindings work, proceed to per-light + ball-mask + galaxy sample
//   no red / magenta / no-op            => shadow cbuffer not bound in post; fall back to ISceneCustomPass
//
HEADER
{
    Description = "Ball shadow tint (spike: red-tints any real shadow)";
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

    // The ball's colour texture (galaxy nebula) - sampled per-ray for the shadow tint.
    Texture2D g_tGalaxy < Attribute( "BallGalaxyTex" ); SrgbRead( true ); >;
    SamplerState g_sGalaxy < Filter( BILINEAR ); AddressU( WRAP ); AddressV( CLAMP ); >;

    float  g_flStrength < Attribute( "BallShadowStrength" ); Default( 1.0f ); >;
    float3 g_vGlassTint < Attribute( "BallGlassTint" ); Default3( 0.82f, 1.0f, 0.88f ); >; // vmat g_vColorTint lift
    float3 g_vTintColor < Attribute( "BallShadowDebugTint" ); Default3( 1.0f, 0.0f, 0.0f ); >; // debug modes only

    // The ball (single-ball stage; arrays come with the multi-ball step).
    float3 g_vBallCenter < Attribute( "BallShadowCenter" ); >;
    float  g_flBallRadius < Attribute( "BallShadowRadius" ); Default( 0.0f ); >; // 0 => no ball, bail
    float4 g_vBallRot    < Attribute( "BallShadowRot" ); Default4( 0.0f, 0.0f, 0.0f, 1.0f ); >; // world rotation quat

    // DIAGNOSTIC MODE (live-switchable uniform - flip in the inspector, no recompile):
    //   1 FULL       : tint the WHOLE screen (pass runs + blit writes back?)
    //   2 DEPTH      : normalized depth greyscale (depth chain bound?)
    //   3 CASCADES   : GREEN if the directional-shadow cbuffer is bound, else RED
    //   4 ALLSHADOWS : red-tint EVERY shadow (the original spike)
    //   5 BALLMASK   : white where the ball's analytic shadow volume covers this pixel
    //   6 FINAL      : the real effect - ball shadow recoloured from the per-ray galaxy sample
    int g_nDebugMode < Attribute( "BallShadowDebugMode" ); Default( 6 ); >;

    // Rotate vector v by quaternion q (xyz,w).
    float3 QuatRotate( float4 q, float3 v )
    {
        return v + 2.0f * cross( q.xyz, cross( q.xyz, v ) + q.w * v );
    }

    // Analytic soft mask: is worldPos inside the ball's shadow volume for a light in direction vToLight?
    // Returns 0..1 (soft rim) and outputs the light-facing surface hit point on the ball.
    float BallShadowMask( float3 vWorldPos, float3 vToLight, out float3 vHit )
    {
        float3 oc = vWorldPos - g_vBallCenter;
        float b = dot( oc, vToLight );                 // closest-approach param is -b
        float perp2 = max( dot( oc, oc ) - b * b, 0.0f );
        float d = sqrt( perp2 );

        // The far intersection (toward the light) - the surface the light enters.
        float c = dot( oc, oc ) - g_flBallRadius * g_flBallRadius;
        float disc = b * b - c;
        float tFar = -b + sqrt( max( disc, 0.0f ) );
        vHit = vWorldPos + vToLight * tFar;

        // Soft only outside the rim; gated to "the ball is toward the light" (closest approach ahead: -b > 0).
        float ahead = step( 0.0f, -b );
        float mask = 1.0f - smoothstep( g_flBallRadius * 0.75f, g_flBallRadius * 1.06f, d );
        return mask * ahead;
    }

    // Sample the galaxy texel the light ray passed through: world hit -> ball-local -> spherical UV.
    float3 SampleGalaxy( float3 vHit )
    {
        // Inverse-rotate the world hit normal into ball-local space, then spherical UV.
        // Constants: 0.15915494 = 1 over two pi, 0.31830989 = 1 over pi.
        float3 n = normalize( vHit - g_vBallCenter );
        float3 nl = normalize( QuatRotate( float4( -g_vBallRot.xyz, g_vBallRot.w ), n ) );
        float u = atan2( nl.y, nl.x ) * 0.15915494f + 0.5f;
        float v = acos( clamp( nl.z, -1.0f, 1.0f ) ) * 0.31830989f;
        return g_tGalaxy.Sample( g_sGalaxy, float2( u, v ) ).rgb * g_vGlassTint;
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
            float d = Depth::GetNormalized( i.vPositionSs.xy );
            return float4( d.xxx, 1.0f );
        }

        if ( g_nDebugMode == 3 )
        {
            float3 c = ( g_DirectionalLightCascadeCount > 0 ) ? float3( 0, 1, 0 ) : float3( 1, 0, 0 );
            return float4( c, 1.0f );
        }

        float3 vWorldPos = Depth::GetWorldPosition( i.vPositionSs.xy );
        float flVisibility = DirectionalLightShadow::GetVisibility( vWorldPos, i.vPositionSs.xy );
        float flShadow = saturate( 1.0f - flVisibility );

        if ( g_nDebugMode == 4 )
        {
            color.rgb = lerp( color.rgb, g_vTintColor, flShadow * g_flStrength );
            return color;
        }

        // No ball this frame -> untouched.
        if ( g_flBallRadius <= 0.0f )
            return color;

        // Direction from this surface point toward the sun (light travels along g_DirectionalLightDirection).
        float3 vToSun = normalize( -g_DirectionalLightDirection.xyz );

        float3 vHit;
        float mask = BallShadowMask( vWorldPos, vToSun, vHit );

        if ( g_nDebugMode == 5 )
            return float4( mask.xxx, 1.0f );

        // FINAL: multiplicative recolour of the REAL shadow, only inside the ball's shadow volume,
        // tinted by the exact galaxy texel the sun ray passed through. flShadow carries the grading.
        float3 vTintFilter = SampleGalaxy( vHit );
        float t = flShadow * mask * g_flStrength;
        color.rgb = lerp( color.rgb, color.rgb * vTintFilter, t );
        return color;
    }
