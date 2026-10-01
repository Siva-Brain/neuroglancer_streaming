Shader "Brain/FusedRaymarch"
{
    // RGB fused-volume ray-marcher for a locally loaded brightfield volume (white
    // background, near-white tissue, black = no data). Object-space march inside a
    // unit cube, same as Brain/T1Raymarch, but colour comes from the voxel's RGB
    // and opacity from how far it is from white:  density = 1 - max(r,g,b).
    //   * max(rgb) < _EmptyCut  -> empty / no-data (black)  -> skip (transparent)
    //   * density  < _Low       -> near-white haze           -> skip
    //   * otherwise window [_Low.._High] -> opacity, colour = rgb  (see-through glass)
    // Mirrors volume.html's RGB path. _Flip mirrors an axis without touching voxels.
    Properties
    {
        _VolumeTex ("Volume (RGB24)", 3D) = "" {}
        _Low ("Density floor (near-white haze)", Range(0, 0.5)) = 0.05
        _High ("Window high", Range(0.05, 1)) = 0.5
        _EmptyCut ("Empty cut (black = no data)", Range(0, 0.2)) = 0.04
        _Density ("Density", Range(0, 3)) = 0.3
        _Steps ("Steps", Float) = 160
        _Flip ("Flip axes (x,y,z = 1 to mirror)", Vector) = (0, 0, 0, 0)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 1  // 1 = Front
        [NoScaleOffset] _LabelTex ("Label ids (R8)", 3D) = "black" {}
        [NoScaleOffset] _Lut ("Region LUT (256x1)", 2D) = "black" {}
        _LabelMask ("Mask to labels (id>0)", Int) = 0
        _LabelColor ("Tint by region colour", Int) = 0
        _LabelAlpha ("Region tint strength", Range(0, 1)) = 0.6
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
            sampler3D _LabelTex;
            sampler2D _Lut;
            float _Low, _High, _EmptyCut, _Density, _Steps, _LabelAlpha;
            int _LabelMask, _LabelColor;
            float4 _Flip;

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
                float invRange = 1.0 / max(_High - _Low, 1e-4);
                float3 outC = 0.0; float outA = 0.0;

                [loop] for (int s = 0; s < N; s++)
                {
                    float3 p = camObj + d * (t.x + (s + 0.5) * dt);
                    float3 q = lerp(p, 1.0 - p, _Flip.xyz);
                    // region label (NEAREST) -- mask out non-tissue (background/fusion slabs)
                    float lid = tex3Dlod(_LabelTex, float4(q, 0)).r;   // id/255
                    if (_LabelMask == 1 && lid < 0.002) continue;      // label 0 = not a region

                    float3 c = tex3Dlod(_VolumeTex, float4(q, 0)).rgb;
                    float mx = max(max(c.r, c.g), c.b);
                    if (mx < _EmptyCut) continue;          // black = no data -> transparent
                    float dens = 1.0 - mx;                 // white bg -> ~0, tissue -> >0
                    if (dens < _Low) continue;             // near-white haze -> skip
                    float v = saturate((dens - _Low) * invRange);
                    float a = saturate(v * _Density);
                    if (_LabelColor == 1 && lid >= 0.002)  // tint by region colour
                    {
                        float3 lc = tex2Dlod(_Lut, float4((lid * 255.0 + 0.5) / 256.0, 0.5, 0, 0)).rgb;
                        c = lerp(c, lc, _LabelAlpha);
                    }
                    outC += (1.0 - outA) * a * c;          // fused colour (optionally region-tinted)
                    outA += (1.0 - outA) * a;
                    if (outA > 0.985) break;
                }
                return fixed4(outC, outA);
            }
            ENDCG
        }
    }
}
