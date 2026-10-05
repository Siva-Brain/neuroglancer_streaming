Shader "Brain/GlassSlide"
{
    // The glass slide that cuts the brain (LeapSliceSlide), drawn with Graphics.DrawMeshNow after the volumes
    // (the volumes write no depth, so no depth test: like OverlayUnlit). The mesh is a thin pane in world space:
    // the two big faces have uv 0..1 across the pane, the four thin side faces have vertex colour r = 1.
    //   faces: a faint tinted body, more opaque at grazing angles (Fresnel), bevelled bright edges and two
    //          sheen streaks that slide across as the viewer's head moves (the SRD tracks the eyes);
    //   sides: the glass edge, seen as a bright cyan line when the pane is edge-on.
    // _Highlight tints the edges / rim (held or a hand at the tab).
    Properties
    {
        _Tint ("Glass body colour (a = base opacity)", Color) = (0.70, 0.88, 0.95, 0.06)
        _EdgeColor ("Edge / bevel colour", Color) = (0.78, 0.97, 1.00, 0.85)
        _Highlight ("Highlight colour", Color) = (0.95, 0.35, 0.85, 1)
        _HighlightAmount ("Highlight amount", Range(0, 1)) = 0
        _Fresnel ("Fresnel opacity", Range(0, 1)) = 0.35
        _Sheen ("Sheen strength", Range(0, 1)) = 0.22
        _Bevel ("Bevel width (uv x, uv y)", Vector) = (0.03, 0.03, 0, 0)
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

            fixed4 _Tint, _EdgeColor, _Highlight;
            float _HighlightAmount, _Fresnel, _Sheen;
            float4 _Bevel;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float3 wp : TEXCOORD0; float3 n : TEXCOORD1; float2 uv : TEXCOORD2; float side : TEXCOORD3; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.n = UnityObjectToWorldNormal(v.normal);
                o.uv = v.uv;
                o.side = v.color.r;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.n);
                float3 v = normalize(_WorldSpaceCameraPos - i.wp);
                float ndv = abs(dot(n, v));
                float fres = pow(1 - ndv, 4);
                float3 edgeCol = lerp(_EdgeColor.rgb, _Highlight.rgb, _HighlightAmount);

                // the thin sides: the glass edge
                if (i.side > 0.5)
                    return fixed4(edgeCol, _EdgeColor.a * (0.55 + 0.45 * ndv));

                // bevel: 1 at the pane's border, 0 inside
                float2 d = min(i.uv, 1 - i.uv) / max(_Bevel.xy, 1e-4);
                float border = 1 - saturate(min(d.x, d.y));
                float rim = smoothstep(0.55, 1.0, border);           // bright outer line
                float bevel = border * border * 0.5;                  // soft inner glow toward it

                // sheen: two diagonal streaks, shifted by the view direction (parallax as the head moves)
                float3 t = normalize(cross(n, float3(0, 1, 0.001)));
                float shift = dot(v, t) * 0.35 + v.y * 0.25;
                float s = (i.uv.x * 0.8 + i.uv.y) * 0.5 + shift;
                float w1 = (frac(s) - 0.35) / 0.09, w2 = (frac(s) - 0.52) / 0.018;
                float wide = exp(-w1 * w1);
                float thin = exp(-w2 * w2);
                float sheen = (wide * 0.6 + thin) * _Sheen;

                float3 col = _Tint.rgb + sheen + fres * 0.35;
                col = lerp(col, edgeCol, saturate(rim + bevel * 0.6));
                float a = _Tint.a + fres * _Fresnel + sheen * 0.5 + rim * _EdgeColor.a + bevel * 0.25
                        + _HighlightAmount * 0.04;
                return fixed4(col, saturate(a));
            }
            ENDCG
        }
    }
}
