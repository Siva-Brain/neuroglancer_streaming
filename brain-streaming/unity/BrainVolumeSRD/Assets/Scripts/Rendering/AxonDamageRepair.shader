Shader "Brain/AxonDamageRepair"
{
    // AxonDamageRepairVolume (V4) on a unit cube (0..1). RGBA32 texture in (i, j, k) order; the cube's
    // local axes are x = i (Right), y = k (Superior), z = j (Anterior), so the lookup is uvw = (x, z, y).
    //   R = healthy fibre presence, G = APP value x presence (0..2 -> 0..1), B = GAP43 presence.
    // Per sample: fibres are blue; inside _AppRadius (mm, soft noisy front around _CentreMm) APP >=
    // _AppLightFrom turns them orange -> red; inside _GapRadius GAP43 shows green on top.
    // Front-to-back, opacity per 10 cm of world length, premultiplied OVER, ZTest Always (drawn after the brain).
    Properties
    {
        _Volume ("Volume (RGBA)", 3D) = "" {}
        _Steps ("Steps", Range(64, 1024)) = 500
        _Density ("Density (per 10 cm)", Range(0, 400)) = 25
        _Brightness ("Brightness", Range(0, 4)) = 2.2
        _Jitter ("Ray Jitter", Range(0, 1)) = 1
        _HealthyColor ("Healthy", Color) = (0.28, 0.42, 0.78, 1)
        _AppLightColor ("APP light", Color) = (1, 0.62, 0.22, 1)
        _AppDenseColor ("APP dense", Color) = (0.95, 0.16, 0.1, 1)
        _GapColor ("GAP43", Color) = (0.25, 1, 0.4, 1)
        _AppLightFrom ("APP light from", Float) = 0.95
        _AppDenseFrom ("APP dense from", Float) = 1.2
        _GapBoost ("GAP43 boost", Float) = 5
        _HealthyAmount ("Healthy amount", Range(0, 1)) = 1
        _AppRadius ("APP radius (mm)", Float) = 1000
        _GapRadius ("GAP43 radius (mm)", Float) = 1000
        _FrontSoft ("Front softness (mm)", Float) = 14
        _FrontNoise ("Front noise (mm)", Float) = 9
        _SizeMm ("Size (mm, object axes)", Vector) = (170, 120, 188, 0)
        _CentreMm ("Stroke centre (mm, object axes)", Vector) = (57, 75, 91, 0)
        _TexSize ("Texture Size", Vector) = (356, 392, 251, 0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Front

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler3D _Volume;
            float _Steps, _Density, _Brightness, _Jitter, _AppLightFrom, _AppDenseFrom, _GapBoost;
            float _HealthyAmount, _AppRadius, _GapRadius, _FrontSoft, _FrontNoise;
            float4 _HealthyColor, _AppLightColor, _AppDenseColor, _GapColor, _SizeMm, _CentreMm, _TexSize;

            struct v2f { float4 pos : SV_POSITION; float3 obj : TEXCOORD0; float4 scr : TEXCOORD1; };

            v2f vert (float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.obj = vertex.xyz;
                o.scr = ComputeScreenPos(o.pos);
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

            float hash (float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            // smooth value noise in 0..1 (cell size 1)
            float h3 (float3 c) { return frac(sin(dot(c, float3(127.1, 311.7, 74.7))) * 43758.5453); }
            float noise3 (float3 p)
            {
                float3 c = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = lerp(lerp(h3(c), h3(c + float3(1, 0, 0)), f.x), lerp(h3(c + float3(0, 1, 0)), h3(c + float3(1, 1, 0)), f.x), f.y);
                float b = lerp(lerp(h3(c + float3(0, 0, 1)), h3(c + float3(1, 0, 1)), f.x), lerp(h3(c + float3(0, 1, 1)), h3(c + float3(1, 1, 1)), f.x), f.y);
                return lerp(a, b, f.z);
            }

            // 1 inside the radius, 0 outside, soft noisy front
            float reveal (float d, float radius)
            {
                return 1.0 - smoothstep(radius - _FrontSoft, radius, d);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rd = normalize(i.obj - camObj);
                float2 t = hitBox(camObj, rd);
                t.x = max(t.x, 0.0);
                if (t.y <= t.x) discard;

                int steps = max((int)_Steps, 1);
                float stepSize = 1.7320508 / steps;
                float ds = length(mul((float3x3)unity_ObjectToWorld, rd)) * stepSize * 10.0;
                float2 sp = i.scr.xy / max(i.scr.w, 1e-5) * _ScreenParams.xy;
                float tt = t.x + stepSize * _Jitter * hash(sp);

                float3 acc = 0.0;
                float a = 0.0;
                [loop] for (int s = 0; s < 1024; ++s)
                {
                    if (tt >= t.y || s >= steps) break;
                    float3 p = camObj + rd * tt;
                    tt += stepSize;
                    float4 v = tex3Dlod(_Volume, float4(p.x, p.z, p.y, 0));
                    if (v.r + v.b < 0.004) continue;

                    float3 mm = p * _SizeMm.xyz;
                    float d = distance(mm, _CentreMm.xyz) + (noise3(mm * 0.12) - 0.5) * 2.0 * _FrontNoise;
                    float appIn = reveal(d, _AppRadius);
                    float gapIn = reveal(d, _GapRadius);

                    // fibres: blue, or APP+ inside the spread
                    float app = v.r > 0.004 ? v.g / v.r * 2.0 : 0.0;
                    float isApp = smoothstep(_AppLightFrom - 0.05, _AppLightFrom + 0.05, app) * appIn;
                    float dense = saturate((app - _AppDenseFrom) / max(0.01, 2.0 - _AppDenseFrom));
                    float3 appCol = lerp(_AppLightColor.rgb, _AppDenseColor.rgb, dense);
                    float wF = v.r * max(_HealthyAmount, isApp);
                    float3 cF = lerp(_HealthyColor.rgb, appCol, isApp);
                    // dense APP is a bit more opaque, like the red clumps in the reference video
                    wF *= 1.0 + 0.6 * isApp * dense;

                    float wG = v.b * gapIn * _GapBoost;
                    float w = wF + wG;
                    if (w <= 1e-4) continue;
                    float3 c = (cF * wF + _GapColor.rgb * wG) / w * _Brightness;

                    float sa = 1.0 - exp(-_Density * w * ds);
                    acc += (1.0 - a) * sa * c;
                    a += (1.0 - a) * sa;
                    if (a > 0.98) break;
                }
                if (a <= 0.0) discard;
                return fixed4(acc, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
