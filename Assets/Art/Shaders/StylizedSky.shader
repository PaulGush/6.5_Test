// Stylized skybox for the day/night cycle: by day a two-tone gradient with a sun disc
// and a warm glow that pools around a low sun; by night a dark gradient with a hashed
// procedural starfield (no textures) and a moon disc. DayNightCycle drives _Night and
// the two body directions every frame, so the sky, the light, and every peer agree.
Shader "Sea/StylizedSky"
{
    Properties
    {
        _DayZenith("Day Zenith", Color) = (0.30, 0.54, 0.82, 1)
        _DayHorizon("Day Horizon", Color) = (0.72, 0.85, 0.94, 1)
        _NightZenith("Night Zenith", Color) = (0.015, 0.025, 0.06, 1)
        _NightHorizon("Night Horizon", Color) = (0.05, 0.08, 0.15, 1)
        _SunColor("Sun", Color) = (1, 0.95, 0.82, 1)
        _SunGlow("Sun Glow (low sun)", Color) = (1, 0.58, 0.32, 1)
        _MoonColor("Moon", Color) = (0.85, 0.90, 1, 1)
        _StarDensity("Star Density", Range(0, 1)) = 0.4
        _Night("Night (driven)", Range(0, 1)) = 0
        _SunDir("Sun Direction (driven)", Vector) = (0, 1, 0, 0)
        _MoonDir("Moon Direction (driven)", Vector) = (0, -1, 0, 0)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _DayZenith;
            half4 _DayHorizon;
            half4 _NightZenith;
            half4 _NightHorizon;
            half4 _SunColor;
            half4 _SunGlow;
            half4 _MoonColor;
            float _StarDensity;
            float _Night;
            float4 _SunDir;
            float4 _MoonDir;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            float Hash13(float3 p)
            {
                return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453);
            }

            // Perpendicular distance to a regular N-gon's edge around a sky body: build a
            // 2D frame across the body's direction, then measure against the nearest edge
            // plane. A constant threshold on this carves a FACETED disc — the low-poly
            // answer to a circle. Rays on the far side are pushed out of range.
            float PolyDist(float3 d, float3 body, float rot, float sides)
            {
                float3 r0 = abs(body.y) > 0.98 ? float3(1, 0, 0)
                                               : normalize(cross(float3(0, 1, 0), body));
                float3 u0 = cross(body, r0);
                float2 uv = float2(dot(d, r0), dot(d, u0));
                float ang = atan2(uv.y, uv.x) + rot;
                float seg = 6.2831853 / sides;
                float poly = cos(seg * floor(ang / seg + 0.5) - ang);
                return length(uv) * poly + (dot(d, body) > 0 ? 0.0 : 1e5);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.dir = IN.positionOS.xyz; // skybox mesh: object position IS the view direction
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 d = normalize(IN.dir);
                float3 sunDir = normalize(_SunDir.xyz);
                float3 moonDir = normalize(_MoonDir.xyz);

                // Vertical gradients, horizon-weighted.
                float up = pow(saturate(d.y + 0.06), 0.62);
                half3 day = lerp(_DayHorizon.rgb, _DayZenith.rgb, up);
                half3 night = lerp(_NightHorizon.rgb, _NightZenith.rgb, up);

                // A low sun pools warm glow around itself: sunset without any extra state.
                float toSun = saturate(dot(d, sunDir));
                float lowSun = 1.0 - saturate(sunDir.y * 2.8);
                day = lerp(day, _SunGlow.rgb, pow(toSun, 3.0) * lowSun * 0.65);

                half3 col = lerp(day, night, _Night);

                // Starfield: sparse DIAMONDS (L1 distance, not round dots) on a 3D hash
                // grid over the view sphere, gently twinkling, faded near the horizon.
                float3 cell = floor(d * 42.0);
                float3 h = float3(Hash13(cell), Hash13(cell + 17.0), Hash13(cell + 43.0));
                float sel = step(1.0 - _StarDensity * 0.14, h.x);
                float3 starPos = (float3(h.y, h.z, Hash13(cell + 91.0)) - 0.5) * 0.8;
                float3 sv = abs(frac(d * 42.0) - 0.5 - starPos);
                float sdist = sv.x + sv.y + sv.z;
                float twinkle = 0.75 + 0.25 * sin(_Time.y * (2.0 + 4.0 * h.y) + h.z * 40.0);
                float star = sel * smoothstep(0.13 + 0.11 * h.y, 0.02, sdist) * twinkle;
                col += star * _Night * saturate(d.y * 1.6 + 0.15) * half3(0.9, 0.95, 1.0);

                // Sun: a faceted hexagon with two stepped halo rings — banded geometry in
                // the Synty language, not a smooth bloom. Slightly rotated so the top edge
                // isn't screen-horizontal.
                float sunD = PolyDist(d, sunDir, 0.26, 6.0);
                col += _SunColor.rgb * (1.0 - _Night) * (
                      (1.0 - smoothstep(0.0260, 0.0268, sunD)) * 1.6
                    + (1.0 - smoothstep(0.0460, 0.0468, sunD)) * 0.22
                    + (1.0 - smoothstep(0.0700, 0.0708, sunD)) * 0.10);

                // Moon: a smaller point-up hexagon with one quiet ring.
                float moonD = PolyDist(d, moonDir, 0.5236, 6.0);
                col += _MoonColor.rgb * _Night * (
                      (1.0 - smoothstep(0.0190, 0.0198, moonD))
                    + (1.0 - smoothstep(0.0340, 0.0348, moonD)) * 0.10);

                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
