Shader "Brain/NeuronalLossVolume"
{
    // Neuronal-loss histology block: Built-in RP port of SRD_test's
    // "Universal Render Pipeline/Custom/NeuronalLossVolume" (2026-09-29), same look and numbers.
    // ONE RG 3D texture on a unit cube (0..1 here, -0.5..0.5 in the original):
    //   R = neuronal_loss_roi_smooth  -> soft grey tissue (masked outside the section, so the block
    //                                   keeps the ragged cut edge of the real sample)
    //   G = neuronal_loss_inverse_roi -> the neuronal signal, pink at the core, purple at the edge
    // Densities are per 10 cm of WORLD length, so the look does not change with the block's size,
    // _StepCount or the viewing angle. Premultiplied output, ZTest Always: it is drawn after the
    // brain (FusedVolumeLoader.Drawn), like the other volumes in this project.
    Properties
    {
        _Volume ("Volume (RG)", 3D) = "" {}
        _StepCount ("Step Count", Range(24, 256)) = 96
        _Alpha ("Fade", Range(0, 1)) = 1

        [Header(Tissue  R channel)]
        _TissueColor ("Tissue Colour", Color) = (0.42, 0.42, 0.45, 1)
        _TissueThreshold ("Tissue Threshold", Range(0, 1)) = 0.04
        _TissueDensity ("Tissue Density (per 10 cm)", Range(0, 8)) = 0.45
        _TissueShade ("Tissue Shading by Intensity", Range(0, 1)) = 0.8

        [Header(Neuronal signal  G channel)]
        _LossColorLow ("Signal Colour (edge, purple)", Color) = (0.48, 0.30, 0.62, 1)
        _LossColorHigh ("Signal Colour (core, pink)", Color) = (0.96, 0.52, 0.52, 1)
        _LossThreshold ("Signal Threshold", Range(0, 1)) = 0.62
        _LossSoftness ("Signal Softness", Range(0.01, 1)) = 0.25
        _LossDensity ("Signal Density (per 10 cm)", Range(0, 8)) = 2.4
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 1  // 1 = Front
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend One OneMinusSrcAlpha   // premultiplied OVER
            ZWrite Off
            ZTest Always
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler3D _Volume;
            float _StepCount, _Alpha;
            float4 _TissueColor;
            float _TissueThreshold, _TissueDensity, _TissueShade;
            float4 _LossColorLow, _LossColorHigh;
            float _LossThreshold, _LossSoftness, _LossDensity;

            struct v2f { float4 pos : SV_POSITION; float3 obj : TEXCOORD0; };

            v2f vert (float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.obj = vertex.xyz;
                return o;
            }

            float2 hitBox (float3 o, float3 d)
            {
                float3 inv = 1.0 / d;
                float3 t0 = (0.0 - o) * inv;
                float3 t1 = (1.0 - o) * inv;
                float3 tn = min(t0, t1), tf = max(t0, t1);
                return float2(max(max(tn.x, tn.y), tn.z), min(min(tf.x, tf.y), tf.z));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                if (_Alpha <= 0.001) discard;
                float3 camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rd = normalize(i.obj - camObj);
                float2 t = hitBox(camObj, rd);
                t.x = max(t.x, 0.0);
                if (t.y <= t.x) discard;

                int steps = max((int)_StepCount, 1);
                float stepSize = (t.y - t.x) / steps;
                // optical depth per step scaled by its WORLD length, per 10 cm
                float ds = length(mul((float3x3)unity_ObjectToWorld, rd)) * stepSize * 10.0;

                float3 accumColor = 0.0;
                float accumAlpha = 0.0;
                [loop] for (int s = 0; s < steps; ++s)
                {
                    float3 uvw = camObj + rd * (t.x + (s + 0.5) * stepSize);
                    float2 v = tex3Dlod(_Volume, float4(uvw, 0)).rg;

                    // tissue: masked greyscale, darker where the section is darker
                    float tissue = smoothstep(_TissueThreshold, _TissueThreshold + 0.08, v.r);
                    float ta = 1.0 - exp(-_TissueDensity * tissue * ds);
                    float3 tc = _TissueColor.rgb * lerp(1.0, 0.35 + 0.9 * v.r, _TissueShade);

                    // neuronal signal: only where the section is tissue, ramping in above the threshold
                    float sig = smoothstep(_LossThreshold, _LossThreshold + _LossSoftness, v.g) * tissue;
                    float la = 1.0 - exp(-_LossDensity * sig * ds);
                    float3 lc = lerp(_LossColorLow.rgb, _LossColorHigh.rgb,
                                     saturate((v.g - _LossThreshold) / max(_LossSoftness, 1e-3)));

                    float sa = 1.0 - (1.0 - ta) * (1.0 - la);
                    if (sa <= 1e-4) continue;
                    float3 sc = (ta * (1.0 - la) * tc + la * lc) / sa;

                    accumColor += (1.0 - accumAlpha) * sa * sc;
                    accumAlpha += (1.0 - accumAlpha) * sa;
                    if (accumAlpha > 0.97) break;
                }
                if (accumAlpha <= 0.0) discard;
                return fixed4(accumColor, accumAlpha) * _Alpha;
            }
            ENDCG
        }
    }
    FallBack Off
}
