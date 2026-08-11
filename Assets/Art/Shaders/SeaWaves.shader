// Stylized low-poly sea for the harbor. Three Gerstner waves displace the vertices of a
// dense grid; the fragment stage derives flat per-facet normals from screen-space
// derivatives, so the surface reads as chunky polygon water that matches the Synty art.
// Colors ramp deep -> shallow -> crest with wave height, lit by the main light with a
// small sun glint. No textures, SRP-batcher compatible, fog aware.
Shader "Sea/Waves"
{
    Properties
    {
        _ShallowColor("Shallow Color", Color) = (0.13, 0.45, 0.55, 1)
        _DeepColor("Deep Color", Color) = (0.05, 0.22, 0.33, 1)
        _CrestColor("Crest Color", Color) = (0.72, 0.88, 0.90, 1)
        _WaveAmp("Wave Amplitude (m)", Float) = 0.35
        _WaveTime("Wave Time (synced clock; <0 = local time)", Float) = -1
        _WaveLength("Base Wave Length (m)", Float) = 11
        _WaveSpeed("Wave Speed", Float) = 1.0
        _Choppiness("Choppiness", Range(0, 1)) = 0.5
        _NoiseAmp("Noise Amplitude (m)", Float) = 0.22
        _NoiseScale("Noise Scale (1/m)", Float) = 0.13
        _DetailFadeStart("Detail Fade Start (m)", Float) = 60
        _DetailFadeEnd("Detail Fade End (m)", Float) = 160
        _SpecStrength("Sun Glint", Range(0, 1)) = 0.35
        _FoamColor("Foam Color", Color) = (0.93, 0.97, 0.97, 1)
        _RippleTint("Ripple Foam Blend", Range(0, 1)) = 0.06
        _FoamWidth("Foam Contact Width (m)", Float) = 0.85
        _FoamNoiseScale("Foam Noise Scale (1/m)", Float) = 2.6
        _FacetFadeStart("Facet Shading Fade Start (m)", Float) = 18
        _FacetFadeEnd("Facet Shading Fade End (m)", Float) = 55
    }
    SubShader
    {
        // Transparent queue (still opaque-rendered with depth write): the surface must
        // draw AFTER the depth texture is ready so contact foam can compare against
        // every opaque — hulls, rocks, shorelines, the player treading water.
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForward" }
            Cull Off // the surface must also render as a ceiling when the camera swims under it

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _ShallowColor;
            half4 _DeepColor;
            half4 _CrestColor;
            float _WaveAmp;
            float _WaveTime;
            float _WaveLength;
            float _WaveSpeed;
            float _Choppiness;
            float _NoiseAmp;
            float _NoiseScale;
            float _DetailFadeStart;
            float _DetailFadeEnd;
            float _SpecStrength;
            half4 _FoamColor;
            float _RippleTint;
            float _FoamWidth;
            float _FoamNoiseScale;
            float _FacetFadeStart;
            float _FacetFadeEnd;
            CBUFFER_END

            // Dynamic ripple rings, fed as shader globals by WaterRippleSystem: foam
            // circles expanding from swimmers, hulls, cargo and sharks. Drawn here in
            // the fragment stage so every ring rides the displaced wave surface —
            // a particle quad at mean sea level would slice through the crests.
            // MAX_RIPPLES must match WaterRippleSystem.MaxRipples.
            #define MAX_RIPPLES 96
            float4 _RippleData[MAX_RIPPLES]; // xy: world XZ centre; z: birth (wave clock); w: full-grown radius
            float4 _RippleAux[MAX_RIPPLES];  // x: strength; y: lifetime (s)
            int _RippleCount;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float height : TEXCOORD1;     // 0..1 relative wave height
                float fogFactor : TEXCOORD2;
                float detail : TEXCOORD3;     // 1 near the camera -> 0 in the far field
            };

            // Hash-based value noise, no textures. Coordinates stay small (world / ~8 m),
            // so the sin-hash keeps its precision.
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

            float3 Gerstner(float2 pos, float2 dir, float wavelength, float amp, float t)
            {
                float k = TWO_PI / wavelength;
                float c = sqrt(9.8 / k); // dispersion: long waves travel faster
                float phase = k * (dot(dir, pos) - c * t * _WaveSpeed);
                float s, cph;
                sincos(phase, s, cph);
                return float3(dir.x * cph * amp * _Choppiness,
                              amp * s,
                              dir.y * cph * amp * _Choppiness);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 pw = TransformObjectToWorld(IN.positionOS.xyz);
                // Synced network clock when the game drives it (all peers and the server
                // agree where the crests are); shader-local time keeps editor previews alive.
                float t = _WaveTime >= 0 ? _WaveTime : _Time.y;
                float ts = t * _WaveSpeed;

                float3 off = float3(0, 0, 0);
                off += Gerstner(pw.xz, normalize(float2(0.3, -1.0)), _WaveLength * 1.9, _WaveAmp * 0.35, t);
                off += Gerstner(pw.xz, normalize(float2(1.0, 0.35)), _WaveLength, _WaveAmp * 0.5, t);
                off += Gerstner(pw.xz, normalize(float2(-0.4, 1.0)), _WaveLength * 0.53, _WaveAmp * 0.25, t);
                off += Gerstner(pw.xz, normalize(float2(0.7, -0.8)), _WaveLength * 0.31, _WaveAmp * 0.15, t);

                // Two independent slow fields, multiplied: swell arrives in irregular sets,
                // and the patches themselves drift against each other, so no repeating
                // pattern ever marches across the whole map in lockstep.
                float groups = lerp(0.45, 1.4, VNoise(pw.xz * 0.024 + float2(0.013, 0.008) * ts))
                             * lerp(0.7, 1.3, VNoise(pw.xz * 0.011 - float2(0.006, 0.011) * ts));
                off *= groups;

                // Three octaves of drifting ripple noise on top, centred around zero.
                float ripple = VNoise(pw.xz * _NoiseScale + float2(0.07, 0.045) * ts)
                             + 0.5 * VNoise(pw.xz * (_NoiseScale * 2.3) - float2(0.052, 0.09) * ts)
                             + 0.25 * VNoise(pw.xz * (_NoiseScale * 5.1) + float2(0.11, -0.06) * ts);
                off.y += (ripple * 0.5714 - 0.5) * _NoiseAmp * 2.0;

                // Displacement fades with camera distance: past the fade the mesh cells are
                // bigger than the waves, and undersampled waves alias into smeared streaks.
                // Distant water is calm and flat instead — it is sub-pixel from deck height.
                // 3D distance (not XZ) so a top-down camera doesn't carry full detail out
                // to a fixed radius, and a noise-jittered edge so the transition never
                // traces a readable circle around the camera's nadir.
                float fadeJitter = (VNoise(pw.xz * 0.013) - 0.5) * 60.0;
                float detail = 1.0 - smoothstep(_DetailFadeStart + fadeJitter, _DetailFadeEnd + fadeJitter,
                    distance(pw, _WorldSpaceCameraPos));
                off *= detail;

                pw += off;

                OUT.positionWS = pw;
                OUT.detail = detail;
                OUT.height = saturate(off.y / max(_WaveAmp + _NoiseAmp, 1e-3) * 0.55 + 0.5);
                OUT.positionCS = TransformWorldToHClip(pw);
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN, half facing : VFACE) : SV_Target
            {
                // Flat facet normal from derivatives; winding differs per platform, so keep it up.
                float3 n = normalize(cross(ddy(IN.positionWS), ddx(IN.positionWS)));
                n = n.y >= 0 ? n : -n;
                // Chunky facet lighting is the style up close, but at distance the facets
                // are smaller than pixels and per-facet shading turns the disc mesh's
                // ring/arc topology into pinwheel + circle moire (blatant from top-down).
                // Fade to the analytic up-normal well before the displacement fades; the
                // swell stays visible through the height color ramp and silhouette.
                // Jittered like the displacement fade, so the transition band never forms
                // a coherent ring of half-faceted spokes around the camera.
                float facetJitter = (VNoise(IN.positionWS.xz * 0.041) - 0.5) * 16.0;
                float facetFade = 1.0 - smoothstep(_FacetFadeStart + facetJitter, _FacetFadeEnd + facetJitter,
                    distance(IN.positionWS, _WorldSpaceCameraPos));
                // The disc mesh's apex rides at the camera's XZ (SeaFollowView), and its
                // centre slivers are sub-pixel from ANY height — smooth the small patch
                // around the camera column too, or a mini pinwheel sits at the nadir.
                facetFade *= smoothstep(1.2, 3.5, distance(IN.positionWS.xz, _WorldSpaceCameraPos.xz));
                n = normalize(lerp(float3(0, 1, 0), n, facetFade));

                Light light = GetMainLight();
                half3 base = lerp(_DeepColor.rgb, _ShallowColor.rgb, IN.height);
                base = lerp(base, _CrestColor.rgb, smoothstep(0.82, 1.0, IN.height) * 0.6);

                // The far field has no wave geometry; large soft tone patches keep it from
                // reading as one flat sheet out to the horizon.
                float farPatch = 0.94 + 0.12 * VNoise(IN.positionWS.xz * 0.045);
                base *= lerp(farPatch, 1.0, IN.detail);

                half ndl = saturate(dot(n, light.direction));
                half3 lit = base * (0.35 + 0.65 * ndl) * light.color;

                float3 v = normalize(GetWorldSpaceViewDir(IN.positionWS));
                float3 h = normalize(light.direction + v);
                // Fresnel: water reflects at grazing angles, barely from above. Without
                // this, the smoothed far field turns the sun glint into one huge soft
                // blob when the camera looks straight down.
                float fres = 0.05 + 0.95 * pow(1.0 - saturate(dot(n, v)), 5.0);
                lit += light.color * _SpecStrength * fres * pow(saturate(dot(n, h)), 64);

                // Contact foam: where something opaque sits near the WATERLINE behind this
                // water pixel (hull, rock, shoreline, a swimmer), lick a noisy white band
                // around it. Depth texture holds all opaques because we draw in the
                // transparent queue. The gap is measured VERTICALLY (reconstructed world
                // height vs this fragment's wave height), not along the view ray — a
                // ray-depth test inflates into a ghostly full-body halo around thin
                // shapes like a swimmer at grazing angles.
                if (facing >= 0)
                {
                    float t = _WaveTime >= 0 ? _WaveTime : _Time.y;
                    float ts = t * _WaveSpeed;
                    // Churned-water colour, shared by the contact wash and the ripple
                    // rings: the shallow blue lit like the surface, a touch brighter —
                    // disturbed water reads lighter and bluer, never surf white.
                    half3 shallowLit = _ShallowColor.rgb * (0.35 + 0.65 * ndl) * light.color;
                    half3 stir = lerp(lit, shallowLit * 1.25, 0.75);
                    stir = lerp(stir, _FoamColor.rgb * light.color, _RippleTint);
                    float2 uv = IN.positionCS.xy / _ScaledScreenParams.xy;
                    float raw = SampleSceneDepth(uv);
                    #if !UNITY_REVERSED_Z
                        // OpenGL: depth buffer is 0..1 but clip z is -1..1; remap before
                        // reconstruction or every point lands nowhere near the surface.
                        raw = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
                    #endif
                    float3 scenePos = ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
                    // Vertical gap to the geometry below the surface. Two foam terms:
                    // a crisp lick confined to the waterline crossing itself, and a wide
                    // soft wash that is heavily noise-broken and never saturates — a
                    // prone swimmer sits entirely inside the wide band, and a single
                    // solid band paints the whole body as a white ghost.
                    float gap = IN.positionWS.y - scenePos.y;
                    if (gap >= 0 && gap < _FoamWidth)
                    {
                        float fn = VNoise(IN.positionWS.xz * _FoamNoiseScale + float2(0.23, 0.17) * ts)
                                 + 0.5 * VNoise(IN.positionWS.xz * (_FoamNoiseScale * 2.7) - float2(0.11, 0.29) * ts);

                        // The lick: bright, only within ~18% of the foam width of the line.
                        float lick = 1.0 - saturate(gap / (_FoamWidth * 0.18));
                        lick = smoothstep(0.35, 0.8, lick * (0.55 + 0.45 * fn));

                        // The wash: fades with depth, sparse noise peaks only — stirred
                        // BLUE, not white, so a prone swimmer sits in churned water
                        // rather than a bleached halo. Only the crisp lick at the actual
                        // waterline crossing stays foam white, painted on top.
                        float wash = 1.0 - saturate(gap / _FoamWidth);
                        wash = smoothstep(0.62, 1.0, wash * (0.35 + 0.65 * fn)) * 0.55;

                        lit = lerp(lit, stir, wash);
                        lit = lerp(lit, _FoamColor.rgb * light.color, lick);
                    }

                    // Ripple rings: an expanding, thickening, fading foam circle per
                    // live slot. Ages and radii are tiny per-slot tests, so dead slots
                    // cost almost nothing; the noise break-up runs once on the sum.
                    float rippleFoam = 0.0;
                    [loop]
                    for (int ri = 0; ri < _RippleCount; ri++)
                    {
                        float4 rd = _RippleData[ri];
                        float2 aux = _RippleAux[ri].xy;
                        float age = t - rd.z;
                        if (age < 0.0 || age >= aux.y) continue;
                        float2 toC = IN.positionWS.xz - rd.xy;
                        float distSq = dot(toC, toC);
                        float reach = rd.w + 0.8;
                        if (distSq > reach * reach) continue;

                        float a01 = age / aux.y;
                        // Decelerating expansion: fast splash, then the ring coasts out
                        // while the band widens and the foam thins away.
                        float radius = rd.w * (1.0 - (1.0 - a01) * (1.0 - a01));
                        float width = max(0.22, rd.w * lerp(0.12, 0.3, a01));
                        float ring = 1.0 - saturate(abs(sqrt(distSq) - radius) / width);
                        float fade = 1.0 - a01;
                        rippleFoam += ring * ring * aux.x * fade * fade;
                    }
                    if (rippleFoam > 0.001)
                    {
                        // Same drifting noise family as the contact foam, so rings read
                        // as patchy churned water, not vector-crisp circles. Detail fade
                        // keeps the far field from shimmering with sub-pixel rings.
                        float rn = VNoise(IN.positionWS.xz * (_FoamNoiseScale * 1.9) + float2(0.31, -0.13) * ts);
                        rippleFoam *= (0.55 + 0.75 * rn) * IN.detail;
                        lit = lerp(lit, stir, saturate(rippleFoam));
                    }
                }

                // Seen from below (swimming), the same surface reads as a dimmer ceiling.
                if (facing < 0) lit *= 0.55;

                lit = MixFog(lit, IN.fogFactor);
                return half4(lit, 1);
            }
            ENDHLSL
        }
    }
}
