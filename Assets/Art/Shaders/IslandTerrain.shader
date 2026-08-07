// Stylized low-poly island terrain, painted by the mesh itself: sand at the waterline
// (darkening where it runs underwater), grass above with large noise patches so meadows
// aren't one flat green, and rock wherever the ground is high or steep — so mountains
// and river gorges read as crags with no extra authoring. Flat facet normals from
// screen-space derivatives match the Synty look; lit like the sea shader, fog aware.
Shader "Island/Terrain"
{
    Properties
    {
        _SandColor("Sand", Color) = (0.87, 0.78, 0.55, 1)
        _GrassColor("Grass", Color) = (0.40, 0.58, 0.24, 1)
        _GrassDark("Grass Dark", Color) = (0.26, 0.44, 0.19, 1)
        _RockColor("Rock", Color) = (0.47, 0.44, 0.40, 1)
        _WaterY("Water Level (world Y)", Float) = 0
        _GrassStart("Grass Starts (m above water)", Float) = 1.1
        _GrassBlend("Grass Blend (m)", Float) = 1.8
        _RockStart("Rock Starts (m above water)", Float) = 13
        _RockBlend("Rock Blend (m)", Float) = 6
        _SlopeRock("Rock Slope (facet normal Y)", Range(0, 1)) = 0.62
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _SandColor;
            half4 _GrassColor;
            half4 _GrassDark;
            half4 _RockColor;
            float _WaterY;
            float _GrassStart;
            float _GrassBlend;
            float _RockStart;
            float _RockBlend;
            float _SlopeRock;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float fogFactor : TEXCOORD1;
            };

            float Hash12(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float VNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash12(i), Hash12(i + float2(1, 0)), u.x),
                            lerp(Hash12(i + float2(0, 1)), Hash12(i + float2(1, 1)), u.x), u.y);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Flat facet normal from derivatives; winding differs per platform, so keep it up.
                float3 n = normalize(cross(ddy(IN.positionWS), ddx(IN.positionWS)));
                n = n.y >= 0 ? n : -n;

                float relH = IN.positionWS.y - _WaterY;

                // Sand, darkening as it runs under the water (river beds, shallows).
                half3 col = _SandColor.rgb * lerp(0.62, 1.0, saturate(relH * 0.6 + 1.0));

                // Grass in big soft patches — two noise scales so meadows have variety.
                half3 grass = lerp(_GrassDark.rgb, _GrassColor.rgb,
                    0.35 + 0.65 * VNoise(IN.positionWS.xz * 0.035)
                        * (0.6 + 0.4 * VNoise(IN.positionWS.xz * 0.11)));
                col = lerp(col, grass, smoothstep(_GrassStart, _GrassStart + _GrassBlend, relH));

                // Rock takes over on high ground and anywhere the facet is steep, so peaks
                // and carved gorge walls read as crag without any extra data.
                float rockT = smoothstep(_RockStart, _RockStart + _RockBlend, relH);
                rockT = max(rockT, 1.0 - smoothstep(_SlopeRock - 0.08, _SlopeRock + 0.10, n.y));
                half3 rock = _RockColor.rgb * (0.88 + 0.24 * VNoise(IN.positionWS.xz * 0.07));
                col = lerp(col, rock, rockT);

                Light light = GetMainLight();
                half ndl = saturate(dot(n, light.direction));
                half3 lit = col * (0.35 + 0.65 * ndl) * light.color;
                lit = MixFog(lit, IN.fogFactor);
                return half4(lit, 1);
            }
            ENDHLSL
        }
    }
}
