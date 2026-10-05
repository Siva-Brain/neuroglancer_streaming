Shader "Brain/OverlayCard"
{
    // A label card (CardOverlay) drawn as a textured quad after everything, the brain volumes included: the
    // volumes are drawn in OnRenderObject with ZTest Always, after all scene geometry, so a normal world-space
    // canvas would be painted over wherever the brain is. _MainTex = the card rendered off-screen (premultiplied
    // colour), _Alpha = the card's fade.
    Properties
    {
        _MainTex ("Card", 2D) = "black" {}
        _Alpha ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Alpha;

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
                return tex2D(_MainTex, i.uv) * _Alpha;
            }
            ENDCG
        }
    }
}
