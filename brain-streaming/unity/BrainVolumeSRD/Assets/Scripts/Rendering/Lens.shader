Shader "Brain/Lens"
{
    // The lens box (LeapLens), drawn with Graphics.DrawMeshNow after the volumes (no depth, like OverlayUnlit).
    // The mesh is a quad on the display panel, uv 0..1 across it; _MainTex is the view around the crosshair,
    // rendered by a camera whose up matches the box's, so it maps straight on. A rounded border frames it and a
    // small mark sits at its centre (the crosshair's spot).
    Properties
    {
        _MainTex ("View", 2D) = "black" {}
        _BorderColor ("Border colour", Color) = (0.78, 0.97, 1.00, 0.9)
        _MarkColor ("Centre mark colour", Color) = (0.18, 0.83, 0.75, 0.95)
        _Aspect ("Width / height", Float) = 1
        _Radius ("Corner radius (fraction of the height)", Range(0, 0.5)) = 0.12
        _Border ("Border width (fraction of the height)", Range(0, 0.1)) = 0.018
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
            fixed4 _BorderColor, _MarkColor;
            float _Aspect, _Radius, _Border;

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
                // position in units of the half-height: x -aspect..aspect, y -1..1
                float2 p = (i.uv * 2 - 1) * float2(_Aspect, 1);
                float2 half_ = float2(_Aspect, 1);
                float r = _Radius * 2;
                float2 q = abs(p) - (half_ - r);
                float d = length(max(q, 0)) + min(max(q.x, q.y), 0) - r;   // rounded-box distance (< 0 inside)
                float aa = max(fwidth(d), 1e-4);

                fixed3 c = tex2D(_MainTex, i.uv).rgb;
                // small centre mark: a plus with a gap
                float2 a = abs(p);
                float arm = 0.09, gap = 0.025, w = 0.006;
                float mark = (a.y < w && a.x > gap && a.x < arm) || (a.x < w && a.y > gap && a.y < arm) ? 1 : 0;
                c = lerp(c, _MarkColor.rgb, mark * _MarkColor.a);
                // border
                float border = smoothstep(-_Border * 2 - aa, -_Border * 2 + aa, d);
                c = lerp(c, _BorderColor.rgb, border * _BorderColor.a);
                return fixed4(c, 1 - smoothstep(-aa, aa, d));
            }
            ENDCG
        }
    }
}
