Shader "Brain/SRDBrickRaymarch"
{
    // RGB brick ray-marcher for the offline-bricked Brain-2 SRD viewer. One unit cube
    // per brick, same see-through-glass model as Brain/FusedRaymarch (white background,
    // density = 1 - max(rgb)), but the brick texture carries a 1-voxel APRON (and BC
    // mult-of-4 padding), so the march samples only the brick's CORE sub-region:
    //     uvw = _TexOffset + coreUVW * _TexScale
    // _TexScale = core_size/stored, _TexOffset = apron/stored (per axis). The core
    // boxes tile space exactly (no geometric overlap), so bricks composite cleanly.
    Properties
    {
        _VolumeTex ("Volume (BC3/BC7/RGB)", 3D) = "" {}
        _TexScale  ("core/stored", Vector) = (1,1,1,0)
        _TexOffset ("apron/stored", Vector) = (0,0,0,0)
        _Low ("Density floor (near-white haze)", Range(0, 0.5)) = 0.05
        _High ("Window high", Range(0.05, 1)) = 0.5
        _EmptyCut ("Empty cut (black = no data)", Range(0, 0.2)) = 0.04
        _Density ("Density", Range(0, 3)) = 0.3
        _Steps ("Steps", Float) = 160
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
            float3 _TexScale, _TexOffset;
            float _Low, _High, _EmptyCut, _Density, _Steps;
            float4 _Flip;

            struct v2f { float4 pos : SV_POSITION; float3 obj : TEXCOORD0; };

            v2f vert (float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.obj = vertex.xyz;                 // unit cube [0,1]^3 = brick CORE
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
                float invRange = 1.0 / max(_High - _Low, 1e-4);
                float3 outC = 0.0; float outA = 0.0;

                [loop] for (int s = 0; s < N; s++)
                {
                    float3 p = camObj + d * (t.x + (s + 0.5) * dt);   // core-local [0,1]
                    float3 q = lerp(p, 1.0 - p, _Flip.xyz);
                    float3 uvw = _TexOffset + q * _TexScale;          // apron/pad-aware
                    float3 c = tex3Dlod(_VolumeTex, float4(uvw, 0)).rgb;
                    float mx = max(max(c.r, c.g), c.b);
                    if (mx < _EmptyCut) continue;          // black = no data -> transparent
                    float dens = 1.0 - mx;                 // white bg -> ~0, tissue -> >0
                    if (dens < _Low) continue;             // near-white haze -> skip
                    float v = saturate((dens - _Low) * invRange);
                    float a = saturate(v * _Density);
                    outC += (1.0 - outA) * a * c;
                    outA += (1.0 - outA) * a;
                    if (outA > 0.985) break;
                }
                return fixed4(outC, outA);
            }
            ENDCG
        }
    }
}
