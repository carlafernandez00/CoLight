// CoLight/OcclusionLit
//
// Uses UniversalFragmentPBR — the same PBR function called by URP's Lit shader and
// the Shader Graph Lit target — so rendering quality is identical.
//
// Two occlusion modes (toggle in material inspector):
//   Occlude Direct Light = OFF  →  only indirect/ambient is scaled (same as Shader Graph AO slot)
//   Occlude Direct Light = ON   →  everything (direct + indirect) is scaled uniformly
//
// InputData / SurfaceData fields map directly to the Shader Graph vertex/fragment blocks:
//   SurfaceData.albedo      ↔  Base Color
//   SurfaceData.metallic    ↔  Metallic
//   SurfaceData.smoothness  ↔  Smoothness
//   SurfaceData.normalTS    ↔  Normal (Tangent Space)
//   SurfaceData.emission    ↔  Emission
//   SurfaceData.occlusion   ↔  Ambient Occlusion  ← we drive this with _OcclusionFactor

Shader "CoLight/OcclusionLit"
{
    Properties
    {
        [MainTexture] _BaseMap   ("Albedo", 2D) = "white" {}
        [MainColor]   _BaseColor ("Base Color", Color) = (0.5,0.5,0.5,1)
        _BumpMap      ("Normal Map", 2D) = "bump" {}
        _Metallic     ("Metallic",   Range(0,1)) = 0.0
        _Smoothness   ("Smoothness", Range(0,1)) = 0.5

        [Space(10)]
        [Header(Occlusion)]
        _OcclusionFactor     ("Occlusion Factor", Range(0,1)) = 1.0
        [Toggle] _OccludeDirectLight ("Occlude Direct Light Too", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry"
        }
        LOD 300

        // ── Forward Lit ─────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            // Mirror the keyword set used by URP Lit so all features work identically
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _BumpMap_ST;
                float  _Metallic;
                float  _Smoothness;
                float  _OcclusionFactor;
                float  _OccludeDirectLight;
            CBUFFER_END

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 uvLM       : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float4 tangentWS   : TEXCOORD2; // .w = tangent sign
                float2 uv          : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                half   fogFactor   : TEXCOORD5;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 6);
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs posInputs    = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normalInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS   = normalInputs.normalWS;
                OUT.tangentWS  = float4(normalInputs.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.shadowCoord = GetShadowCoord(posInputs);
                OUT.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);

                OUTPUT_LIGHTMAP_UV(IN.uvLM, unity_LightmapST, OUT.staticLightmapUV);
                OUTPUT_SH(OUT.normalWS, OUT.vertexSH);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Normal mapping — reconstructs world-space normal from tangent-space map
                float3 normalTS  = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv));
                float3 bitangent = IN.tangentWS.w * cross(IN.normalWS, IN.tangentWS.xyz);
                float3 normalWS  = NormalizeNormalPerPixel(
                    TransformTangentToWorld(normalTS, half3x3(IN.tangentWS.xyz, bitangent, IN.normalWS)));

                // ── InputData ────────────────────────────────────────────────
                // Equivalent to the Shader Graph vertex context outputs
                InputData inputData = (InputData)0;
                inputData.positionWS              = IN.positionWS;
                inputData.positionCS              = IN.positionCS;
                inputData.normalWS                = normalWS;
                inputData.viewDirectionWS         = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord             = IN.shadowCoord;
                inputData.fogCoord                = IN.fogFactor;
                inputData.vertexLighting          = half3(0, 0, 0);
                inputData.bakedGI                 = SAMPLE_GI(IN.staticLightmapUV, IN.vertexSH, normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask              = SAMPLE_SHADOWMASK(IN.staticLightmapUV);
                inputData.tangentToWorld          = half3x3(IN.tangentWS.xyz, bitangent, normalWS);

                // ── SurfaceData ──────────────────────────────────────────────
                // Each field maps 1:1 to a Shader Graph fragment block
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo      = albedo.rgb;
                surfaceData.metallic    = _Metallic;
                surfaceData.smoothness  = _Smoothness;
                surfaceData.normalTS    = normalTS;
                surfaceData.emission    = half3(0, 0, 0);
                surfaceData.alpha       = albedo.a;

                // Occlusion mode selection (no GPU branch — both paths evaluated, lerp picks one)
                //   _OccludeDirectLight = 0 → surfaceData.occlusion = ao, no post-scale
                //                             indirect occluded inside UniversalFragmentPBR (same as graph AO slot)
                //   _OccludeDirectLight = 1 → surfaceData.occlusion = 1 (no internal occlusion),
                //                             post-multiply by ao applies to direct + indirect equally
                half ao = saturate(_OcclusionFactor);
                surfaceData.occlusion = lerp(ao, 1.0, _OccludeDirectLight);

                // Full URP PBR: shadows, point/spot lights, reflection probes, lightmaps, SSAO
                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                // Post-scale for full occlusion mode (no-op when _OccludeDirectLight = 0)
                color.rgb *= lerp(1.0, ao, _OccludeDirectLight);

                color.rgb = MixFog(color.rgb, IN.fogFactor);
                return color;
            }
            ENDHLSL
        }

        // Reuse URP Lit's shadow caster and depth passes directly — no need to rewrite them
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }
}
