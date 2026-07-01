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

    // Live side-on silhouette of the opaque occupant (Terry), captured to a render target by
    // OccupantSilhouetteCapture. Projected per light so he casts his real shape. Non-black texel = Terry.
    Texture2D g_tOccupant < Attribute( "OccupantSilhouette" ); >;
    SamplerState g_sOccupant < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

    float g_flStrength < Attribute( "BallShadowStrength" ); Default( 1.0f ); >;
    float g_flTerryCut < Attribute( "BallShadowTerryCut" ); Default( 0.92f ); >; // occlusion above this, under an occupant ball, darkens
    float g_flOccupantDark < Attribute( "BallShadowOccupantDark" ); Default( 0.1f ); >; // opaque-occupant darkness in the SUN shadow (0 = pure black)
    float g_flColorize < Attribute( "BallShadowColorize" ); Default( 0.5f ); >;   // 0 = multiply (dark, shadow-like), 1 = colorize (vibrant)
    float g_flSaturation < Attribute( "BallShadowSaturation" ); Default( 0.65f ); >; // 1 = full colour, 0 = greyscale shadow
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

    // Extra coloured point lights (beyond the sun), a dynamic count up to a component-set cap. Pos = (xyz world,
    // w = range/radius); Col = rgb. Each casts an ANALYTIC ball-colour shadow (ball surface colour x light
    // colour), faded out past the light's range. Terry's silhouette inside is the ENGINE's real shadow (these
    // lights cast real shadows), already in the colour buffer - we don't re-derive it. StructuredBuffer => N dynamic.
    struct ShadowLight
    {
        float4 Pos;
        float4 Col;
    };
    StructuredBuffer<ShadowLight> g_lights < Attribute( "ShadowLights" ); >;
    int g_nLightCount < Attribute( "ShadowLightCount" ); Default( 0 ); >;

    // DIAGNOSTIC MODE (live-switchable uniform - flip in the inspector, no recompile):
    //   1 FULL / 2 DEPTH / 3 CASCADES / 4 ALLSHADOWS / 5 BALLMASK / 6 FINAL (the real effect)
    int g_nDebugMode < Attribute( "BallShadowDebugMode" ); Default( 6 ); >;

    float3 QuatRotate( float4 q, float3 v )
    {
        return v + 2.0f * cross( q.xyz, cross( q.xyz, v ) + q.w * v );
    }

    // Analytic soft mask: is worldPos in this ball's shadow volume for a light in direction vToLight?
    // 0..1 (thin AA edge) and outputs BOTH surface points the ray crosses: vHitFar (sun-facing entry) and
    // vHitNear (ground-facing exit). Sampling both captures light that passed through two colours.
    float BallShadowMask( float3 vWorldPos, float3 vToLight, float3 center, float radius, out float3 vHitFar, out float3 vHitNear )
    {
        float3 oc = vWorldPos - center;
        float b = dot( oc, vToLight );
        float perp2 = max( dot( oc, oc ) - b * b, 0.0f );
        float d = sqrt( perp2 );

        float c = dot( oc, oc ) - radius * radius;
        float disc = b * b - c;
        float s = sqrt( max( disc, 0.0f ) );
        vHitFar  = vWorldPos + vToLight * ( -b + s ); // sun-facing surface
        vHitNear = vWorldPos + vToLight * ( -b - s ); // ground-facing surface

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

    // Coverage of the occupant's captured silhouette for a light: billboard the side-on Terry cutout to FACE
    // the light (about world-up), intersect the shadow ray with that vertical plane through the ball centre,
    // and read the silhouette there. Gives Terry's real shape as a planar shadow that shears with light angle.
    // Frame matches the capture: [centre +/- radius] maps to the texture, feet at the bottom, head at the top.
    float OccupantSilhouette( float3 wp, float3 toL, float3 center, float radius )
    {
        float3 up = float3( 0, 0, 1 );
        float3 h = toL - up * dot( toL, up ); // horizontal part of the light direction
        float hl = length( h );
        if ( hl < 0.05f ) return 0.0f;        // near-overhead light: no meaningful side silhouette
        h /= hl;
        float3 right = normalize( cross( up, h ) );

        float denom = dot( toL, h );
        if ( denom < 0.05f ) return 0.0f;
        float t = dot( center - wp, h ) / denom;
        if ( t <= 0.0f ) return 0.0f;         // plane is behind us
        float3 d = ( wp + toL * t ) - center; // hit on Terry's billboard plane, relative to the ball centre

        float u = dot( d, right ) / radius * 0.5f + 0.5f;
        float v = dot( d, up ) / radius * 0.5f + 0.5f;
        if ( u < 0.0f || u > 1.0f || v < 0.0f || v > 1.0f ) return 0.0f;

        float3 s = g_tOccupant.SampleLevel( g_sOccupant, float2( u, 1.0f - v ), 0 ).rgb; // texture V=0 is the top (head)
        float lum = dot( s, float3( 0.299f, 0.587f, 0.114f ) );
        return smoothstep( 0.03f, 0.10f, lum );
    }

    // Stack one ball into the subtractive filter, grow the union mask, and (if it holds an opaque occupant)
    // the occupant mask - the full ball disc, which the SUN path gates with the real shadow map to carve out
    // Terry. The extra lights don't use occ: Terry's silhouette there is his projected captured silhouette.
    void AccumBall( Texture2D tex, float3 wp, float3 toL, float4 a, float4 t, float4 rot,
                    inout float3 filter, inout float mask, inout float occ )
    {
        float radius = a.w;
        if ( radius <= 0.0f )
            return;

        float3 hitFar, hitNear;
        float m = BallShadowMask( wp, toL, a.xyz, radius, hitFar, hitNear );
        if ( m <= 0.0f )
            return;

        // Average the entry + exit surface colours: same-colour chord keeps its colour, a chord crossing
        // two colours greys toward their mean (light passed through both) rather than picking just one.
        float3 col = 0.5f * ( SampleBall( tex, hitFar, a.xyz, rot ) + SampleBall( tex, hitNear, a.xyz, rot ) ) * t.rgb;
        filter *= lerp( float3( 1, 1, 1 ), col, m );             // subtractive: overlapping balls multiply
        mask = max( mask, m );
        if ( t.w > 0.5f )
            occ = max( occ, m );
    }

    // Cast one extra point light's shadow onto the ground: the ANALYTIC ball-colour shadow (ball surface colour
    // filtering the light) plus TERRY's projected captured silhouette darkening on top. Both fade out past the
    // light's range so the ball stops throwing shadows once it has rolled out of the light.
    void ApplyLight( float3 wp, float4 lpos, float4 lcol, inout float3 color )
    {
        float radius = lpos.w;
        if ( radius <= 0.0f )
            return;

        // Range falloff: (1 - (d/r)^2)^2, so the shadow is full near the light and gone at its radius.
        float3 toLight = lpos.xyz - wp;
        float atten = saturate( 1.0f - dot( toLight, toLight ) / ( radius * radius ) );
        atten *= atten;
        if ( atten <= 0.0f )
            return;

        float3 toL = normalize( toLight );

        // --- Ball colour projection (within the ball's shadow disc) ---
        float3 filter = float3( 1, 1, 1 );
        float mask = 0.0f;
        float occ = 0.0f;
        AccumBall( g_tBall0, wp, toL, g_vBallA0, g_vBallT0, g_vBallR0, filter, mask, occ );
        AccumBall( g_tBall1, wp, toL, g_vBallA1, g_vBallT1, g_vBallR1, filter, mask, occ );
        AccumBall( g_tBall2, wp, toL, g_vBallA2, g_vBallT2, g_vBallR2, filter, mask, occ );
        AccumBall( g_tBall3, wp, toL, g_vBallA3, g_vBallT3, g_vBallR3, filter, mask, occ );
        if ( mask > 0.0f )
        {
            float flum = dot( filter, float3( 0.299f, 0.587f, 0.114f ) );
            filter = lerp( float3( flum, flum, flum ), filter, g_flSaturation );

            float3 transmit = filter * lcol.rgb; // the ball colour filtering this light's colour
            float baseLuma = dot( color, float3( 0.299f, 0.587f, 0.114f ) );
            float3 tinted = lerp( color * transmit, baseLuma * transmit, g_flColorize );
            color = lerp( color, tinted, mask * g_flStrength * atten );
        }

        // --- Terry (opaque occupant) silhouette shadow: his real captured shape, projected along this light.
        // Darkens the ground toward OccupantDark; can extend beyond the ball disc (a long figure shadow).
        float terry = 0.0f;
        if ( g_vBallT0.w > 0.5f ) terry = max( terry, OccupantSilhouette( wp, toL, g_vBallA0.xyz, g_vBallA0.w ) );
        if ( g_vBallT1.w > 0.5f ) terry = max( terry, OccupantSilhouette( wp, toL, g_vBallA1.xyz, g_vBallA1.w ) );
        if ( g_vBallT2.w > 0.5f ) terry = max( terry, OccupantSilhouette( wp, toL, g_vBallA2.xyz, g_vBallA2.w ) );
        if ( g_vBallT3.w > 0.5f ) terry = max( terry, OccupantSilhouette( wp, toL, g_vBallA3.xyz, g_vBallA3.w ) );
        if ( terry > 0.0f )
            color = lerp( color, color * g_flOccupantDark, terry * atten * g_flStrength );
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

        if ( g_nDebugMode == 7 )
            return float4( filter * mask, 1.0f ); // raw projected ball colour (isolates texture vs composite)

        // Opaque occupant (Terry) inside an occupant ball reads black: where THAT ball's mask overlaps a
        // saturated real shadow, force the tint to black. Colour balls (no occupant) keep their colour.
        float opaque = smoothstep( g_flTerryCut, 1.0f, flShadow ) * occ;
        filter = lerp( filter, float3( g_flOccupantDark, g_flOccupantDark, g_flOccupantDark ), opaque ); // deep grey, not pure black

        // Desaturate the projected colour toward its own luminance so the shadow reads muted, not neon.
        float flum = dot( filter, float3( 0.299f, 0.587f, 0.114f ) );
        filter = lerp( float3( flum, flum, flum ), filter, g_flSaturation );

        // Paint the projected colour across the footprint. Multiply (color*filter) darkens the ground like a
        // real shadow; colorize (baseLuma*filter) keeps the hue in dark areas - blend for a muted colour shadow.
        float baseLuma = dot( color.rgb, float3( 0.299f, 0.587f, 0.114f ) );
        float3 tinted = lerp( color.rgb * filter, baseLuma * filter, g_flColorize );
        color.rgb = lerp( color.rgb, tinted, mask * g_flStrength );

        // Extra coloured lights each cast their own analytic ball shadow on top (N up to the component cap).
        [loop]
        for ( int li = 0; li < g_nLightCount; li++ )
        {
            ShadowLight L = g_lights[li];
            ApplyLight( vWorldPos, L.Pos, L.Col, color.rgb );
        }
        return color;
    }
}
