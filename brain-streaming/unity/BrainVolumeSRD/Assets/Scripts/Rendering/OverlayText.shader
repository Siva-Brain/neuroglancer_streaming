Shader "Brain/OverlayText"
{
    // Text drawn with Graphics.DrawMeshNow after the volumes (no depth, like OverlayUnlit): a quad per glyph,
    // uvs into a dynamic font's texture, whose alpha is the glyph. Used for LeapLens' coordinate readout.
    Properties
    {
        _MainTex ("Font texture", 2D) = "white" {}
        _Color ("Colour", Color) = (1, 1, 1, 1)
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
            fixed4 _Color;

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
                return fixed4(_Color.rgb, _Color.a * tex2D(_MainTex, i.uv).a);
            }
            ENDCG
        }
    }
}
