Shader "Brain/BlockRaymarch"
{
    // Merged-block volume ray-marcher for the SRD client. Marches ONE box per
    // block (not ~58 slab bricks). The volume is RGBA with colour baked into rgb
    // and opacity into a (baked on the DGX), so the fragment shader just samples
    // and composites -- no per-step tissue/density/colour math. A tiny occupancy
    // volume (_OccTex) gates the expensive volume fetch so empty background costs
    // almost nothing. Marches in OBJECT space (the unit cube), invariant to the
    // per-block matrix and BrainRoot.
    Properties
    {
        _VolumeTex ("Volume (RGBA)", 3D) = "" {}
        _OccTex ("Occupancy (R)", 3D) = "" {}
        _OccScale ("Occ scale", Vector) = (1,1,1,0)
        _Density ("Density", Float) = 8
        _Steps ("Steps", Float) = 192
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
            sampler3D _OccTex;
            float3 _OccScale;
            float _Density;
            float _Steps;

            struct v2f { float4 pos : SV_POSITION; float3 obj : TEXCOORD0; };

            v2f vert (float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.obj = vertex.xyz;               // unit-cube [0,1] object position
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
                float3 outC = 0.0; float outA = 0.0;

                [loop] for (int s = 0; s < N; s++)
                {
                    float3 p = camObj + d * (t.x + (s + 0.5) * dt);
                    // empty-space skip: tiny occupancy fetch gates the volume fetch
                    if (tex3Dlod(_OccTex, float4(p * _OccScale, 0)).r < 0.02) continue;
                    float4 v = tex3Dlod(_VolumeTex, float4(p, 0));   // rgb=colour, a=opacity
                    float a = saturate(v.a * _Density * dt);
                    if (a > 0.0)
                    {
                        outC += (1.0 - outA) * a * v.rgb;
                        outA += (1.0 - outA) * a;
                        if (outA > 0.985) break;
                    }
                }
                return fixed4(outC, outA);   // premultiplied
            }
            ENDCG
        }
    }
}
