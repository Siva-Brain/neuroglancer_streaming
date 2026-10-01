Shader "Brain/FusedRaymarch"
{
    // RGB fused-volume ray-marcher for a locally loaded brightfield volume (white
    // background, pale stained tissue). Object-space march inside a unit cube, same
    // as Brain/T1Raymarch, but 3-channel colour.
    //
    // Opacity comes from COLOUR SATURATION, not darkness. Measured on hb02: the
    // background is pure white (sat < 8/255) while tissue is only slightly darker
    // (max(rgb) 224-240) but clearly coloured (sat 24-56). 1 - max(rgb) gave tissue
    // ~0.06-0.12 -> a faint white fog; saturation separates the two cleanly.
    //   * max(rgb) < _EmptyCut              -> no data (black)   -> skip
    //   * sat = max - min, window [_SatLow.._SatHigh] -> opacity
    //   * colour = pow(rgb, _Gamma)         -> deepens the pale stain
    //   * optional gradient shading (_Shade) -> gyri/sulci read as a surface
    //   * per-pixel ray jitter (_Jitter)    -> removes wood-grain moire rings
    // The loader turns black no-data voxels white on load, so missing Zarr chunks do
    // not leave grey sheets where trilinear filtering blends black into white.
    // _Flip mirrors an axis without touching voxels. _ClipMin/_ClipMax limit the
    // march to a sub-box (sagittal slicing).
    Properties
    {
        _VolumeTex ("Volume (RGB24)", 3D) = "" {}
        _SatLow ("Saturation floor (background)", Range(0, 0.3)) = 0.06
        _SatHigh ("Saturation for full opacity", Range(0.02, 0.6)) = 0.2
        _EmptyCut ("Empty cut (black = no data)", Range(0, 0.2)) = 0.04
        _Density ("Density", Range(0, 3)) = 0.6
        _Gamma ("Colour gamma (deepen stain)", Range(1, 6)) = 2.5
        _Brightness ("Brightness", Range(0.2, 3)) = 1.2
        _Shade ("Surface shading", Range(0, 1)) = 0.7
        _Jitter ("Ray jitter", Range(0, 1)) = 1
        _Steps ("Steps", Float) = 256
        _TexSize ("Texture size (voxels)", Vector) = (256, 256, 256, 0)
        _Flip ("Flip axes (x,y,z = 1 to mirror)", Vector) = (0, 0, 0, 0)
        _ClipMin ("Clip box min (unit cube)", Vector) = (0, 0, 0, 0)
        _ClipMax ("Clip box max (unit cube)", Vector) = (1, 1, 1, 0)
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
            float _SatLow, _SatHigh, _EmptyCut, _Density, _Gamma, _Brightness, _Shade, _Jitter, _Steps;
            float4 _TexSize, _Flip;
            float4 _ClipMin, _ClipMax;   // visible sub-box of the unit cube (slicing)
            sampler3D _LabelTex;
            sampler2D _Lut;
            float _LabelAlpha;
            int _LabelMask, _LabelColor;

            struct v2f { float4 pos : SV_POSITION; float3 obj : TEXCOORD0; };

            v2f vert (float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.obj = vertex.xyz;
                return o;
            }

            float2 hitBox (float3 o, float3 d, float3 bmin, float3 bmax)
            {
                float3 inv = 1.0 / d;
                float3 t0 = (bmin - o) * inv;
                float3 t1 = (bmax - o) * inv;
                float3 tn = min(t0, t1), tf = max(t0, t1);
                return float2(max(max(tn.x, tn.y), tn.z), min(min(tf.x, tf.y), tf.z));
            }

            // tissue measure: colour saturation, 0 for white background and black no-data
            float tissue (float3 q)
            {
                float3 c = tex3Dlod(_VolumeTex, float4(q, 0)).rgb;
                float mx = max(max(c.r, c.g), c.b);
                if (mx < _EmptyCut) return 0.0;
                return mx - min(min(c.r, c.g), c.b);
            }

            float hash12 (float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 d = normalize(i.obj - camObj);
                // March only the visible sub-box, so the full step budget lands on
                // the remaining tissue and the cut face starts exactly at the plane.
                float2 t = hitBox(camObj, d, _ClipMin.xyz, _ClipMax.xyz);
                t.x = max(t.x, 0.0);
                if (t.y <= t.x) discard;

                int N = (int)_Steps;
                float dt = (t.y - t.x) / N;
                float t0 = t.x + dt * (_Jitter * hash12(i.pos.xy) + 0.5 * (1.0 - _Jitter));
                float invRange = 1.0 / max(_SatHigh - _SatLow, 1e-4);

                // headlight in world space (view direction, nudged up) for shading
                float3 dW = normalize(mul((float3x3)unity_ObjectToWorld, d));
                float3 L = normalize(-dW + float3(0, 0.4, 0));
                float3 h = 1.0 / max(_TexSize.xyz, 1.0);   // one voxel in texture space

                float3 outC = 0.0; float outA = 0.0;
                [loop] for (int s = 0; s < N; s++)
                {
                    float3 p = camObj + d * (t0 + s * dt);
                    float3 q = lerp(p, 1.0 - p, _Flip.xyz);
                    // region label (NEAREST) -- mask out non-tissue (background/fusion slabs)
                    float lid = tex3Dlod(_LabelTex, float4(q, 0)).r;   // id/255
                    if (_LabelMask == 1 && lid < 0.002) continue;      // label 0 = not a region

                    float3 c = tex3Dlod(_VolumeTex, float4(q, 0)).rgb;
                    float mx = max(max(c.r, c.g), c.b);
                    if (mx < _EmptyCut) continue;                  // black = no data
                    float sat = mx - min(min(c.r, c.g), c.b);
                    float v = saturate((sat - _SatLow) * invRange);
                    if (v <= 0.0) continue;                        // white background
                    float a = saturate(v * _Density);

                    float3 col = saturate(pow(c, _Gamma) * _Brightness);
                    if (_LabelColor == 1 && lid >= 0.002)  // tint by region colour
                    {
                        float3 lc = tex2Dlod(_Lut, float4((lid * 255.0 + 0.5) / 256.0, 0.5, 0, 0)).rgb;
                        col = lerp(col, lc, _LabelAlpha);
                    }
                    if (_Shade > 0.0)
                    {
                        float3 g = float3(
                            tissue(q + float3(h.x, 0, 0)) - tissue(q - float3(h.x, 0, 0)),
                            tissue(q + float3(0, h.y, 0)) - tissue(q - float3(0, h.y, 0)),
                            tissue(q + float3(0, 0, h.z)) - tissue(q - float3(0, 0, h.z)));
                        g /= h;                                    // per unit-cube length (axes differ in voxel count)
                        g *= 1.0 - 2.0 * _Flip.xyz;                // back to object space
                        // object -> world normal (inverse transpose), points out of tissue
                        float3 n = mul(-g, (float3x3)unity_WorldToObject);
                        float len = length(n);
                        if (len > 1e-6)
                        {
                            n /= len;
                            float diff = abs(dot(n, L));
                            float lit = 0.35 + 0.75 * diff;
                            col *= lerp(1.0, lit, _Shade);
                        }
                    }

                    outC += (1.0 - outA) * a * col;        // fused colour (optionally region-tinted), shaded
                    outA += (1.0 - outA) * a;
                    if (outA > 0.985) break;
                }
                return fixed4(outC, outA);
            }
            ENDCG
        }
    }
}
