Shader "Brain/Raymarch"
{
    // Volume brick ray-marcher. Marches in OBJECT space (the unit cube), so it is
    // invariant to the per-brick matrix and the BrainRoot transform (scale/rotate).
    // Samples RG: R = grayscale (ch0), G = tissue mask (ch3). Matches the browser
    // reference client: tissue = 1 - R, density = G * tissue.
    Properties
    {
        _VolumeTex ("Volume (RG or RGB)", 3D) = "" {}
        _Density ("Density", Float) = 8
        _Steps ("Steps", Float) = 48
        _RGB ("RGB fused (1) vs Nissl RG (0)", Float) = 0
        // If the brain is invisible or looks inside-out, flip this in the material
        // (Front renders the box's far faces -> valid inside & outside).
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
            Cull [_Cull]                 // default Front -> far faces (valid inside & outside)

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler3D _VolumeTex;
            float _Density;
            float _Steps;
            float _RGB;

            struct v2f { float4 pos : SV_POSITION; float3 obj : TEXCOORD0; };

            v2f vert (float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.obj = vertex.xyz;               // unit-cube [0,1] object position
                return o;
            }

            // ray vs unit cube [0,1]^3
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
                    float4 tex = tex3Dlod(_VolumeTex, float4(p, 0));
                    float dens; float3 col;
                    if (_RGB > 0.5)
                    {
                        // RGB fused (hb02): white background, darker = tissue. Matches
                        // the browser client: density = 1 - max(rgb); colour = rgb.
                        float3 rgb = tex.rgb;
                        float mx = max(max(rgb.r, rgb.g), rgb.b);
                        if (mx < 0.04) continue;          // empty / no-data (black) -> skip
                        dens = 1.0 - mx;
                        col = rgb;
                    }
                    else
                    {
                        // Nissl gray+mask: tissue = 1 - gray, density = mask * tissue.
                        float tissue = 1.0 - tex.r;
                        dens = tex.g * tissue;
                        col = lerp(float3(0.55, 0.62, 0.78), float3(0.98, 0.98, 0.98), tissue);
                    }
                    if (dens > 0.02)
                    {
                        float a = saturate(dens * _Density * dt);
                        outC += (1.0 - outA) * a * col;
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
