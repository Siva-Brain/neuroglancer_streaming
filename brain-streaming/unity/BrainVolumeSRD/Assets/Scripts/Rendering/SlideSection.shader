Shader "Brain/SlideSection"
{
    // A slide image (IipSectionOverlay) on the brain's cut face, drawn with Graphics.DrawMeshNow after the volumes
    // (no depth, like OverlayUnlit). Only the stained tissue shows: the slide's white background (low colour
    // saturation) and anything outside the image are transparent. _Reveal limits it to a growing circle
    // (the transition as the cut face reaches the slide's section).
    Properties
    {
        _MainTex ("Slide", 2D) = "white" {}
        _Alpha ("Opacity", Range(0, 1)) = 1
        _SatLow ("Saturation: background below", Range(0, 0.3)) = 0.03
        _SatHigh ("Saturation: full opacity from", Range(0, 0.5)) = 0.08
        _Reveal ("Reveal: centre uv, radius, soft edge", Vector) = (0.5, 0.5, 10, 0.01)
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
            float _Alpha, _SatLow, _SatHigh;
            float4 _Reveal;

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
                if (any(i.uv < 0) || any(i.uv > 1)) discard;
                fixed3 c = tex2D(_MainTex, i.uv).rgb;
                float sat = max(max(c.r, c.g), c.b) - min(min(c.r, c.g), c.b);
                float a = smoothstep(_SatLow, _SatHigh, sat) * _Alpha;
                // the transition (IipSectionOverlay): only inside a circle growing from the section's middle, soft edge
                a *= 1 - smoothstep(_Reveal.z - _Reveal.w, _Reveal.z, distance(i.uv, _Reveal.xy));
                if (a <= 0.001) discard;
                return fixed4(c, a);
            }
            ENDCG
        }
    }
}
