Shader "Brain/T1Raymarch"
{
    // Single-channel (R8) MRI ray-marcher for a locally loaded volume such as a T1.
    // Same structure as Brain/Raymarch (object-space march inside a unit cube) but
    // maps one grayscale channel through a window (_Low.._High) so bright tissue
    // becomes dense; background below _Low is transparent. _Flip mirrors an axis
    // without touching the voxel data (for L/R or up/down corrections).
    Properties
    {
        _VolumeTex ("Volume (R8)", 3D) = "" {}
        _Low ("Threshold (below = air)", Range(0, 1)) = 0.1
        _High ("Window high", Range(0, 1)) = 1
        _Density ("Density", Range(0, 5)) = 1
        _Steps ("Steps", Float) = 128
        _Tint ("Tint", Color) = (0.85, 0.65, 0.55, 1)
        _Flip ("Flip axes (x,y,z = 1 to mirror)", Vector) = (0, 0, 0, 0)
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

            sampler3D _VolumeTex;
            float _Low, _High, _Density, _Steps;
            float4 _Tint, _Flip;

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
                float3 camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 d = normalize(i.obj - camObj);
                float2 t = hitBox(camObj, d);
                t.x = max(t.x, 0.0);
                if (t.y <= t.x) discard;

                int N = (int)_Steps;
                float dt = (t.y - t.x) / N;
                float invRange = 1.0 / max(_High, 1e-4);
                float3 outC = 0.0; float outA = 0.0;

                [loop] for (int s = 0; s < N; s++)
                {
                    float3 p = camObj + d * (t.x + (s + 0.5) * dt);
                    float3 q = lerp(p, 1.0 - p, _Flip.xyz);
                    float v = tex3Dlod(_VolumeTex, float4(q, 0)).r;
                    if (v >= _Low)                 // below threshold = air, skip
                    {
                        v = saturate(v * invRange);
                        // per-sample opacity independent of step length (matches the
                        // StrokeVideo VolumeRaymarch look: solid surface, not fog)
                        float a = saturate(v * _Density);
                        float3 col = _Tint.rgb * v;
                        outC += (1.0 - outA) * a * col;
                        outA += (1.0 - outA) * a;
                        if (outA > 0.985) break;
                    }
                }
                return fixed4(outC, outA);
            }
            ENDCG
        }
    }
}
