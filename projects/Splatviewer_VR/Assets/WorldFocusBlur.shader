Shader "Hidden/Custom/WorldFocusBlur"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "WorldFocusBlur"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ _LINEAR_TO_SRGB_CONVERSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ShaderVariablesFunctions.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            SAMPLER(sampler_BlitTexture);
            TEXTURECUBE(_WorldFocusSkyboxCubemap);
            SAMPLER(sampler_WorldFocusSkyboxCubemap);

            float _WorldFocusEffectEnabled;
            float4 _WorldFocusForwardWS;
            float4 _WorldFocusRightWS;
            float4 _WorldFocusUpWS;
            float4 _WorldFocusOriginWS;
            float4 _WorldFocusCameraPositionWS;
            float4 _WorldFocusCameraForwardWS;
            float4 _WorldFocusCameraRightWS;
            float4 _WorldFocusCameraUpWS;
            float4 _WorldFocusParams;
            float4 _WorldFocusCameraParams;
            float4 _WorldFocusRectParams;
            float4 _WorldFocusDisplayParams;
            float4 _WorldFocusSkyboxZenithColor;
            float4 _WorldFocusSkyboxHorizonColor;
            float4 _WorldFocusSkyboxGroundColor;
            float4 _WorldFocusExcludeRect0;
            float4 _WorldFocusExcludeRect1;
            float4 _WorldFocusExcludeParams;

            float3 GetPixelWorldDirection(float2 uv)
            {
                float2 ndc = uv * 2.0 - 1.0;
                float tanHalfFovY = _WorldFocusCameraParams.x;
                float aspect = _WorldFocusCameraParams.y;

                float3 cameraForward = SafeNormalize(_WorldFocusCameraForwardWS.xyz);
                float3 cameraRight = SafeNormalize(_WorldFocusCameraRightWS.xyz);
                float3 cameraUp = SafeNormalize(_WorldFocusCameraUpWS.xyz);
                return SafeNormalize(
                    cameraForward +
                    cameraRight * ndc.x * tanHalfFovY * aspect +
                    cameraUp * ndc.y * tanHalfFovY);
            }

            float GetRectangularFocusMask(float3 pixelDirWS)
            {
                float3 focusForward = SafeNormalize(_WorldFocusForwardWS.xyz);
                float3 focusRight = SafeNormalize(_WorldFocusRightWS.xyz);
                float3 focusUp = SafeNormalize(_WorldFocusUpWS.xyz);
                float3 focusVector = pixelDirWS;

                if (_WorldFocusDisplayParams.w > 0.5)
                    return dot(pixelDirWS, focusForward) >= 0.0 ? 0.0 : 1.0;

                if (_WorldFocusRectParams.w > 0.5)
                {
                    float3 cameraPosWS = _WorldFocusCameraPositionWS.xyz;
                    float3 focusOriginWS = _WorldFocusOriginWS.xyz;
                    float3 focusPlaneWS = focusOriginWS + focusForward * _WorldFocusRectParams.z;
                    float denom = dot(pixelDirWS, focusForward);

                    if (abs(denom) < 1e-5)
                        return 1.0;

                    float rayDistance = dot(focusPlaneWS - cameraPosWS, focusForward) / denom;
                    if (rayDistance <= 0.0)
                        return 1.0;

                    float3 pointOnFocusPlaneWS = cameraPosWS + pixelDirWS * rayDistance;
                    focusVector = pointOnFocusPlaneWS - focusOriginWS;
                }

                float focusDepth = dot(focusVector, focusForward);
                if (focusDepth <= 1e-5)
                    return 1.0;

                float xSlope = abs(dot(focusVector, focusRight) / focusDepth);
                float ySlope = abs(dot(focusVector, focusUp) / focusDepth);
                float xMask = smoothstep(_WorldFocusParams.x, _WorldFocusRectParams.x, xSlope);
                float yMask = smoothstep(_WorldFocusParams.y, _WorldFocusRectParams.y, ySlope);
                return saturate(max(xMask, yMask));
            }

            half4 SampleSkybox(float3 pixelDirWS)
            {
                if (_WorldFocusDisplayParams.y > 0.5)
                    return SAMPLE_TEXTURECUBE(_WorldFocusSkyboxCubemap, sampler_WorldFocusSkyboxCubemap, pixelDirWS) * _WorldFocusDisplayParams.z;

                float upAmount = pixelDirWS.y;
                half3 aboveHorizon = lerp(_WorldFocusSkyboxHorizonColor.rgb, _WorldFocusSkyboxZenithColor.rgb, saturate(upAmount));
                half3 belowHorizon = lerp(_WorldFocusSkyboxGroundColor.rgb, _WorldFocusSkyboxHorizonColor.rgb, saturate(upAmount + 1.0));
                half3 skyColor = lerp(belowHorizon, aboveHorizon, step(0.0, upAmount));
                return half4(skyColor * _WorldFocusDisplayParams.z, 1.0h);
            }

            float IsInsideRect(float2 uv, float4 rect)
            {
                if (rect.z <= rect.x || rect.w <= rect.y)
                    return 0.0;

                float2 lower = step(rect.xy, uv);
                float2 upper = step(uv, rect.zw);
                return lower.x * lower.y * upper.x * upper.y;
            }

            half4 SampleBlur(float2 uv, float2 texel, float radius)
            {
                float2 offset = texel * radius;
                half4 center = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv) * 4.0h;
                half4 sum = center;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + float2( offset.x, 0.0));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + float2(-offset.x, 0.0));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + float2(0.0,  offset.y));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + float2(0.0, -offset.y));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + float2( offset.x,  offset.y));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + float2(-offset.x,  offset.y));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + float2( offset.x, -offset.y));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + float2(-offset.x, -offset.y));
                return sum / 12.0h;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 baseColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv);

                if (_WorldFocusExcludeParams.x > 0.5)
                {
                    float protectedArea = max(IsInsideRect(uv, _WorldFocusExcludeRect0), IsInsideRect(uv, _WorldFocusExcludeRect1));
                    if (protectedArea > 0.5)
                        return baseColor;
                }

                if (_WorldFocusEffectEnabled < 0.5)
                    return baseColor;

                float3 pixelDirWS = GetPixelWorldDirection(uv);
                float blurT = GetRectangularFocusMask(pixelDirWS);

                float radius = blurT * _WorldFocusParams.z;
                if (blurT <= 0.001)
                    return baseColor;

                half4 peripheralColor;
                if (_WorldFocusDisplayParams.x > 0.5)
                {
                    peripheralColor = SampleSkybox(pixelDirWS);
                }
                else
                {
                    float2 texel = _BlitTexture_TexelSize.xy;
                    peripheralColor = SampleBlur(uv, texel, radius);
                }

                half4 result = lerp(baseColor, peripheralColor, blurT);
                result.rgb *= (1.0 - blurT * _WorldFocusParams.w);

                #ifdef _LINEAR_TO_SRGB_CONVERSION
                result = LinearToSRGB(result);
                #endif

                return result;
            }
            ENDHLSL
        }
    }
}
