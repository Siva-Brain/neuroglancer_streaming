Shader "Brain/BrickMask"
{
    // Ray-marcher for a bricked MASK volume (BrickMaskOverlay, e.g. hb02_vessels): the brick's alpha is the mask
    // (0 = none, 1 = inside), drawn in _Color as shaded, nearly opaque surfaces (the mask's gradient is the
    // normal). Same brick conventions as Brain/SRDBrickRaymarch: one unit cube per brick CORE,
    //     uvw = _TexOffset + core * _TexScale,
    // front faces culled so the camera may be inside, and only the part kept by the brain's slice is marched
    // (_ClipMin / _ClipMax, brick unit cube). _Steps ~ one per voxel so thin vessels are not skipped.
    Properties
    {
        _VolumeTex ("Mask bricks (alpha = mask)", 3D) = "" {}
        _TexScale  ("core/stored", Vector) = (1,1,1,0)
        _TexOffset ("apron/stored", Vector) = (0,0,0,0)
        _TexSize   ("stored size (voxels)", Vector) = (256,256,256,0)
        _Color ("Colour", Color) = (0.86, 0.16, 0.16, 1)
        _Density ("Opacity per voxel step", Range(0, 1)) = 0.6
        _Shade ("Surface shading", Range(0, 1)) = 0.8
        _Steps ("Steps across the brick", Float) = 512
        _ClipMin ("Kept box min (brick unit cube)", Vector) = (0, 0, 0, 0)
        _ClipMax ("Kept box max (brick unit cube)", Vector) = (1, 1, 1, 0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend One OneMinusSrcAlpha   // premultiplied OVER
            ZWrite Off
            ZTest Always
            Cull Front

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler3D _VolumeTex;
            float3 _TexScale, _TexOffset, _TexSize;
            fixed4 _Color;
            float _Density, _Shade, _Steps;
            float4 _ClipMin, _ClipMax;

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
                float3 t0 = (bmin - o) * inv, t1 = (bmax - o) * inv;
                float3 tn = min(t0, t1), tf = max(t0, t1);
                return float2(max(max(tn.x, tn.y), tn.z), min(min(tf.x, tf.y), tf.z));
            }

            float mask (float3 uvw) { return tex3Dlod(_VolumeTex, float4(uvw, 0)).a; }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 d = normalize(i.obj - camObj);
                float2 t = hitBox(camObj, d, _ClipMin.xyz, _ClipMax.xyz);
                t.x = max(t.x, 0.0);
                if (t.y <= t.x) discard;

                // a fixed step (~ one voxel across the brick), so the opacity per step is the same everywhere
                float dt = 1.0 / max(_Steps, 1.0);
                int N = (int)min((t.y - t.x) / dt, 4096);
                float3 dW = normalize(mul((float3x3)unity_ObjectToWorld, d));
                float3 L = normalize(-dW + float3(0, 0.4, 0));
                float3 h = 1.0 / max(_TexSize, 1.0);

                float3 outC = 0.0; float outA = 0.0;
                [loop] for (int s = 0; s < N; s++)
                {
                    float3 p = camObj + d * (t.x + (s + 0.5) * dt);
                    float3 uvw = _TexOffset + p * _TexScale;
                    float m = mask(uvw);
                    if (m < 0.35) continue;
                    float a = saturate((m - 0.35) / 0.3) * _Density;
                    float3 col = _Color.rgb;
                    if (_Shade > 0.0)
                    {
                        float3 g = float3(mask(uvw + float3(h.x, 0, 0)) - mask(uvw - float3(h.x, 0, 0)),
                                          mask(uvw + float3(0, h.y, 0)) - mask(uvw - float3(0, h.y, 0)),
                                          mask(uvw + float3(0, 0, h.z)) - mask(uvw - float3(0, 0, h.z)));
                        g *= _TexSize * _TexScale;
                        float3 n = mul(-g, (float3x3)unity_WorldToObject);
                        float len = length(n);
                        if (len > 1e-6)
                        {
                            n /= len;
                            float lit = 0.3 + 0.9 * saturate(dot(n, L)) + 0.25 * pow(saturate(dot(reflect(-L, n), -dW)), 12);
                            col *= lerp(1.0, lit, _Shade);
                        }
                    }
                    outC += (1.0 - outA) * a * col;
                    outA += (1.0 - outA) * a;
                    if (outA > 0.985) break;
                }
                if (outA <= 0.0) discard;
                return fixed4(outC, outA) * _Color.a;   // premultiplied: the colour fades with the alpha
            }
            ENDCG
        }
    }
}
