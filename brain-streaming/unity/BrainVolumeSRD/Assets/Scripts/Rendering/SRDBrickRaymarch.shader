Shader "Brain/SRDBrickRaymarch"
{
    // RGB brick ray-marcher for the offline-bricked Brain-2 SRD viewer. One unit cube
    // per brick. The brick texture carries a 1-voxel APRON (and BC mult-of-4 padding), so
    // the march samples only the brick's CORE sub-region:
    //     uvw = _TexOffset + coreUVW * _TexScale
    // _TexScale = core_size/stored, _TexOffset = apron/stored (per axis). The core boxes
    // tile space exactly (no geometric overlap), so bricks composite cleanly.
    //
    // Transfer function = Brain/FusedRaymarch's (2026-10-01): opacity from COLOUR
    // SATURATION, not darkness. On hb02 the background is pure white (sat < 8/255) and
    // tissue only slightly darker (max(rgb) 224-240) but clearly coloured (sat 24-56);
    // 1 - max(rgb) made tissue a faint white fog. Grey (black no-data blended into white
    // by filtering, or a grey padding sheet) has ~0 saturation, so it is transparent too.
    //   * max(rgb) < _EmptyCut              -> no data (black)   -> skip
    //   * sat = max - min, window [_SatLow.._SatHigh] -> opacity
    //   * colour = pow(rgb, _Gamma)         -> deepens the pale stain
    //   * gradient shading (_Shade), per-pixel ray jitter (_Jitter)
    // Opacity is per unit length of the WHOLE volume: each sample's alpha is corrected by
    // its step length in volume units (_BrickToVol = core size / volume size per axis)
    // against a reference of 1/_RefSteps, so a brick looks the same however many steps it
    // takes and however big it is (before, every brick took _Steps samples over its own
    // small box and over-accumulated).
    // Tissue mask (_UseMask): the BC3 bricks still contain the stitching-seam planes, so
    // BrickVolumeLoader builds a whole-volume mask from an exported level with the seam filter
    // applied; samples outside it are transparent. Looked up at _BrickMinVol + q * _BrickToVol.
    Properties
    {
        _VolumeTex ("Volume (BC3/BC7/RGB)", 3D) = "" {}
        _TexScale  ("core/stored", Vector) = (1,1,1,0)
        _TexOffset ("apron/stored", Vector) = (0,0,0,0)
        _TexSize   ("stored size (voxels)", Vector) = (256,256,256,0)
        _BrickToVol ("core size / volume size", Vector) = (1,1,1,0)
        _SatLow ("Saturation floor (background)", Range(0, 0.3)) = 0.06
        _SatHigh ("Saturation for full opacity", Range(0.02, 0.6)) = 0.2
        _EmptyCut ("Empty cut (black = no data)", Range(0, 0.2)) = 0.04
        _Density ("Density", Range(0, 3)) = 0.6
        _RefSteps ("Reference steps across the volume", Float) = 256
        _Gamma ("Colour gamma (deepen stain)", Range(1, 6)) = 2.5
        _Brightness ("Brightness", Range(0.2, 3)) = 1.2
        _Shade ("Surface shading", Range(0, 1)) = 0.7
        _Jitter ("Ray jitter", Range(0, 1)) = 1
        _Steps ("Steps per brick", Float) = 160
        _Flip ("Flip axes (x,y,z = 1 to mirror)", Vector) = (0, 0, 0, 0)
        _ClipMin ("Kept box min (brick unit cube, slicing)", Vector) = (0, 0, 0, 0)
        _ClipMax ("Kept box max (brick unit cube, slicing)", Vector) = (1, 1, 1, 0)
        _MaskTex ("Whole-volume tissue mask (R8)", 3D) = "white" {}
        _UseMask ("Use tissue mask", Float) = 0
        _BrickMinVol ("Brick core min corner, whole-volume unit coords", Vector) = (0, 0, 0, 0)
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
            float3 _TexScale, _TexOffset, _TexSize, _BrickToVol;
            float _SatLow, _SatHigh, _EmptyCut, _Density, _RefSteps, _Gamma, _Brightness, _Shade, _Jitter, _Steps;
            float4 _Flip;
            float4 _ClipMin, _ClipMax;   // part of this brick kept by the slice (loader converts the volume cut)
            sampler3D _MaskTex;          // whole-volume tissue mask: 0 outside the tissue (seam planes)
            float _UseMask;
            float3 _BrickMinVol;         // brick core min corner in whole-volume unit coords

            struct v2f { float4 pos : SV_POSITION; float3 obj : TEXCOORD0; };

            v2f vert (float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.obj = vertex.xyz;                 // unit cube [0,1]^3 = brick CORE
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

            // tissue measure: colour saturation, 0 for white background, grey and black no-data
            float tissue (float3 uvw)
            {
                float3 c = tex3Dlod(_VolumeTex, float4(uvw, 0)).rgb;
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
                float2 t = hitBox(camObj, d, _ClipMin.xyz, _ClipMax.xyz);   // march only the kept part
                t.x = max(t.x, 0.0);
                if (t.y <= t.x) discard;

                int N = (int)_Steps;
                float dt = (t.y - t.x) / N;
                float t0 = t.x + dt * (_Jitter * hash12(i.pos.xy) + 0.5 * (1.0 - _Jitter));
                float invRange = 1.0 / max(_SatHigh - _SatLow, 1e-4);
                // this step's length in whole-volume units vs the reference step
                float stepK = length(d * dt * _BrickToVol) * _RefSteps;

                // headlight in world space (view direction, nudged up) for shading
                float3 dW = normalize(mul((float3x3)unity_ObjectToWorld, d));
                float3 L = normalize(-dW + float3(0, 0.4, 0));
                float3 h = 1.0 / max(_TexSize, 1.0);       // one stored voxel in uvw

                float3 outC = 0.0; float outA = 0.0;
                [loop] for (int s = 0; s < N; s++)
                {
                    float3 p = camObj + d * (t0 + s * dt);             // core-local [0,1]
                    float3 q = lerp(p, 1.0 - p, _Flip.xyz);
                    float3 uvw = _TexOffset + q * _TexScale;           // apron/pad-aware
                    float3 c = tex3Dlod(_VolumeTex, float4(uvw, 0)).rgb;
                    float mx = max(max(c.r, c.g), c.b);
                    if (mx < _EmptyCut) continue;                      // black = no data
                    float sat = mx - min(min(c.r, c.g), c.b);
                    float v = saturate((sat - _SatLow) * invRange);
                    if (v <= 0.0) continue;                            // white bg / grey
                    if (_UseMask > 0.5)                                // outside the tissue = seam planes
                    {
                        v *= smoothstep(0.25, 0.75, tex3Dlod(_MaskTex, float4(_BrickMinVol + q * _BrickToVol, 0)).r);
                        if (v <= 0.0) continue;
                    }
                    float a0 = saturate(v * _Density);
                    float a = 1.0 - pow(max(1.0 - a0, 1e-4), stepK);   // step-length corrected

                    float3 col = saturate(pow(c, _Gamma) * _Brightness);
                    if (_Shade > 0.0)
                    {
                        float3 g = float3(
                            tissue(uvw + float3(h.x, 0, 0)) - tissue(uvw - float3(h.x, 0, 0)),
                            tissue(uvw + float3(0, h.y, 0)) - tissue(uvw - float3(0, h.y, 0)),
                            tissue(uvw + float3(0, 0, h.z)) - tissue(uvw - float3(0, 0, h.z)));
                        g *= _TexSize * _TexScale;                     // per brick-object length
                        g *= 1.0 - 2.0 * _Flip.xyz;
                        float3 n = mul(-g, (float3x3)unity_WorldToObject);   // object -> world normal
                        float len = length(n);
                        if (len > 1e-6)
                        {
                            n /= len;
                            float lit = 0.35 + 0.75 * abs(dot(n, L));
                            col *= lerp(1.0, lit, _Shade);
                        }
                    }

                    outC += (1.0 - outA) * a * col;
                    outA += (1.0 - outA) * a;
                    if (outA > 0.985) break;
                }
                return fixed4(outC, outA);
            }
            ENDCG
        }
    }
}
