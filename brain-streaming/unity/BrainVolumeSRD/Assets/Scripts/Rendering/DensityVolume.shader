Shader "Brain/DensityVolume"
{
    // A scalar density map (NpzDensityVolume, e.g. HB02 astrocyte density) on a unit cube (0..1).
    // R8 texture in (i, j, k) order = (Right, Anterior, Superior); the cube's local axes are
    // x = Right, y = Superior, z = Anterior, so the lookup is uvw = (x, z, y).
    // 0 = not sampled (transparent); 1..255 = value / percentile window. Three-stop colour ramp.
    //   _Mode 0 (composite): front-to-back, opacity per 10 cm of WORLD length (independent of the
    //            volume's size and _Steps), headlight gradient shading (_Shade).
    //   _Mode 1 (max intensity): each pixel = the highest value along its ray.
    // Premultiplied OVER, ZTest Always like the project's other volumes.
    Properties
    {
        _Volume ("Volume (R8)", 3D) = "" {}
        _Mode ("Mode (0 composite, 1 MIP)", Float) = 0
        _Steps ("Steps", Range(32, 1024)) = 600
        _Threshold ("Threshold", Range(0, 1)) = 0.08
        _Density ("Density (per 10 cm)", Range(0, 200)) = 40
        _Gamma ("Value Gamma", Range(0.2, 3)) = 0.7
        _ColorLow ("Low", Color) = (0.15, 0.35, 1.00, 1)
        _ColorMid ("Mid", Color) = (0.20, 0.95, 0.70, 1)
        _ColorHigh ("High", Color) = (1.00, 0.85, 0.20, 1)
        _Brightness ("Brightness", Range(0, 3)) = 1.3
        _Shade ("Gradient Shading", Range(0, 1)) = 0.5
        _Jitter ("Ray Jitter", Range(0, 1)) = 1
        _Bounds ("Show Bounds", Float) = 0
        _TexSize ("Texture Size", Vector) = (712, 783, 501, 0)
        _ClipMin ("Kept box min (slicing)", Vector) = (0, 0, 0, 0)
        _ClipMax ("Kept box max (slicing)", Vector) = (1, 1, 1, 0)
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
            float _Mode, _Steps, _Threshold, _Density, _Gamma, _Brightness, _Shade, _Jitter, _Bounds;
            float4 _ColorLow, _ColorMid, _ColorHigh, _TexSize, _ClipMin, _ClipMax;   // kept box (follows the brain's slice)

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
                float3 t0 = (_ClipMin.xyz - o) * inv;
                float3 t1 = (_ClipMax.xyz - o) * inv;
                float3 tn = min(t0, t1), tf = max(t0, t1);
                return float2(max(max(tn.x, tn.y), tn.z), min(min(tf.x, tf.y), tf.z));
            }

            float hash (float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            float3 ramp (float v)
            {
                return v < 0.5 ? lerp(_ColorLow.rgb, _ColorMid.rgb, v * 2.0)
                               : lerp(_ColorMid.rgb, _ColorHigh.rgb, v * 2.0 - 1.0);
            }

            // raw 0..1 texel at an object-space point (swizzled to texture axes)
            float S (float3 p) { return tex3Dlod(_Volume, float4(p.x, p.z, p.y, 0)).r; }

            // 0 = not sampled; else value mapped to 0..1 (texel 1..255) with gamma
            float V (float raw) { return pow(saturate((raw * 255.0 - 1.0) / 254.0), _Gamma); }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rd = normalize(i.obj - camObj);
                float2 t = hitBox(camObj, rd);
                t.x = max(t.x, 0.0);
                if (t.y <= t.x) discard;

                int steps = max((int)_Steps, 1);
                float stepSize = 1.7320508 / steps;   // fixed step in the unit cube
                float ds = length(mul((float3x3)unity_ObjectToWorld, rd)) * stepSize * 10.0;
                float2 sp = i.scr.xy / max(i.scr.w, 1e-5) * _ScreenParams.xy;
                float tt = t.x + stepSize * _Jitter * hash(sp);
                // one voxel along each object axis (object x = tex x, y = tex z, z = tex y)
                float3 e = 1.0 / max(float3(_TexSize.x, _TexSize.z, _TexSize.y), 1.0);

                float3 acc = 0.0;
                float a = 0.0;

                if (_Mode > 0.5)
                {
                    // maximum intensity projection
                    float m = 0.0;
                    [loop] for (int s = 0; s < 1024; ++s)
                    {
                        if (tt >= t.y || s >= steps) break;
                        float raw = S(camObj + rd * tt);
                        tt += stepSize;
                        if (raw * 255.0 >= 0.5) m = max(m, V(raw));
                    }
                    a = smoothstep(_Threshold, _Threshold + 0.1, m) * saturate(0.35 + m);
                    acc = ramp(m) * _Brightness * a;
                }
                else
                {
                    [loop] for (int s = 0; s < 1024; ++s)
                    {
                        if (tt >= t.y || s >= steps) break;
                        float3 p = camObj + rd * tt;
                        float raw = S(p);
                        tt += stepSize;
                        if (raw * 255.0 < 0.5) continue;
                        float v = V(raw);
                        float w = smoothstep(_Threshold, _Threshold + 0.05, v);
                        float sa = 1.0 - exp(-_Density * v * w * ds);
                        if (sa <= 1e-4) continue;
                        float3 c = ramp(v) * _Brightness;
                        if (_Shade > 0.001)
                        {
                            float3 g = float3(S(p + float3(e.x, 0, 0)) - S(p - float3(e.x, 0, 0)),
                                              S(p + float3(0, e.y, 0)) - S(p - float3(0, e.y, 0)),
                                              S(p + float3(0, 0, e.z)) - S(p - float3(0, 0, e.z)));
                            float gl = length(g);
                            float lit = gl > 1e-4 ? 0.35 + 0.65 * abs(dot(g / gl, rd)) : 1.0;
                            c *= lerp(1.0, lit, _Shade);
                        }
                        acc += (1.0 - a) * sa * c;
                        a += (1.0 - a) * sa;
                        if (a > 0.98) break;
                    }
                }

                if (_Bounds > 0.5)
                {
                    // faint edges where the ray enters the box near two faces
                    float3 pe = camObj + rd * t.x;
                    float3 d = min(pe, 1.0 - pe);
                    float edge = (d.x < 0.004) + (d.y < 0.004) + (d.z < 0.004);
                    if (edge >= 2.0) { float ea = 0.5 * (1.0 - a); acc += ea * 0.8; a += ea; }
                }
                if (a <= 0.0) discard;
                return fixed4(acc, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
