//
// Ball shadow tint — SPIKE STAGE.
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

    // Debug tint (defaults to red) + strength, so the spike and the real version share plumbing.
    float3 g_vTintColor < Attribute( "BallShadowDebugTint" ); Default3( 1.0f, 0.0f, 0.0f ); >;
    float  g_flStrength < Attribute( "BallShadowStrength" ); Default( 1.0f ); >;

    // DIAGNOSTIC MODE (live-switchable uniform — flip in the inspector, no recompile):
    //   1 = FULL     : tint the WHOLE screen. Proves the pass runs + the blit writes back.
    //   2 = DEPTH    : show normalized depth as greyscale. Proves the depth chain is bound.
    //   3 = CASCADES : GREEN if the directional-shadow cbuffer is bound (cascadeCount>0), else RED.
    //   4 = SHADOW   : the real test — tint pixels the shadow term reports as shadowed.
    int g_nDebugMode < Attribute( "BallShadowDebugMode" ); Default( 1 ); >;

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float2 vScreenUv = CalculateViewportUv( i.vPositionSs.xy );
        float4 color = g_tColorBuffer.Sample( g_sBilinearMirror, vScreenUv );

        if ( g_nDebugMode == 1 )
        {
            // Whole-screen tint: if you don't see this, the pass isn't running on this view.
            color.rgb = lerp( color.rgb, g_vTintColor, g_flStrength );
            return color;
        }

        if ( g_nDebugMode == 2 )
        {
            // Depth readback sanity: near = black, far = white.
            float d = Depth::GetNormalized( i.vPositionSs.xy );
            return float4( d.xxx, 1.0f );
        }

        if ( g_nDebugMode == 3 )
        {
            // Is the directional-light shadow constant buffer bound during this blit?
            float3 c = ( g_DirectionalLightCascadeCount > 0 ) ? float3( 0, 1, 0 ) : float3( 1, 0, 0 );
            return float4( c, 1.0f );
        }

        // Mode 4 (and anything else): the real shadow test.
        float3 vWorldPos = Depth::GetWorldPosition( i.vPositionSs.xy );
        float flVisibility = DirectionalLightShadow::GetVisibility( vWorldPos, i.vPositionSs.xy );
        float flShadow = saturate( 1.0f - flVisibility );
        color.rgb = lerp( color.rgb, g_vTintColor, flShadow * g_flStrength );
        return color;
}
