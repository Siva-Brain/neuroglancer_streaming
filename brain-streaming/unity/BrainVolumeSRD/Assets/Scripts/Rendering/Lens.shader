Shader "Brain/Lens"
{
    // The magnifying lens (LeapLens), drawn with Graphics.DrawMeshNow after the volumes (no depth, like
    // OverlayUnlit). The mesh is a unit disc facing the viewer, uv 0..1 across it; _MainTex is the zoomed view,
    // rendered by a camera whose axes match the disc's, so it maps straight on. A thin ring outlines the circle.
    Properties
    {
        _MainTex ("Zoomed view", 2D) = "black" {}
        _RingColor ("Outline colour", Color) = (0.78, 0.97, 1.00, 0.95)
        _RingWidth ("Outline width (fraction of radius)", Range(0.01, 0.3)) = 0.05
        _Dots ("Level dots (0 = none)", Float) = 0
        _DotsOn ("Dots lit", Float) = 1
        _Loading ("The last lit dot blinks (level loading)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _RingColor;
            float _RingWidth, _Dots, _DotsOn, _Loading;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float r = length(i.uv * 2 - 1);
                float aa = max(fwidth(r), 1e-4);
                fixed3 c = tex2D(_MainTex, i.uv).rgb;
                float ring = smoothstep(1 - _RingWidth - aa, 1 - _RingWidth + aa, r);
                c = lerp(c, _RingColor.rgb, ring * _RingColor.a);

                // level dots in a row near the bottom, coarse (left) to fine (right): lit up to the current level
                float2 p = i.uv * 2 - 1;
                const float dr = 0.045, gap = 0.13;
                for (int k = 0; k < 8; k++)
                {
                    if (k >= _Dots) break;
                    float2 at = float2((k - (_Dots - 1) * 0.5) * gap, -0.80);
                    float d = length(p - at);
                    float dot = 1 - smoothstep(dr - aa, dr + aa, d);
                    float edge = dot * smoothstep(dr * 0.55 - aa, dr * 0.55 + aa, d);
                    bool lit = k < _DotsOn;
                    float blink = (_Loading > 0.5 && k == (int)_DotsOn - 1) ? 0.35 + 0.65 * step(0.5, frac(_Time.y * 2)) : 1;
                    fixed3 col = lit ? _RingColor.rgb * blink : float3(0, 0, 0);
                    c = lerp(c, col, lit ? dot : max(edge, dot * 0.5));
                    c = lerp(c, _RingColor.rgb * 0.8, lit ? 0 : edge);
                }
                return fixed4(c, 1 - smoothstep(1 - aa, 1 + aa, r));   // anti-aliased edge
            }
            ENDCG
        }
    }
}
