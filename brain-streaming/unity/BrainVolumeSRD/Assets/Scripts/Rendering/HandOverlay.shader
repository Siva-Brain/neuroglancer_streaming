// Ultraleap hands drawn on top of the brain volume (LeapHandRenderer, from the volume's Drawn event).
// The volumes don't write depth, so the hand is depth-tested only against itself and ordinary scene
// geometry: pass 0 writes the hand's depth, pass 1 blends its colour where it is the nearest surface,
// so a see-through hand never shows its own inner overlaps. Soft light from the viewer + a rim.
// The forearm fades out past the wrist: _FadeOrigin (wrist), _FadeDir (towards the elbow, 0 = no fade),
// _FadeLength (world units); the faded part writes no depth either.
Shader "Brain/HandOverlay"
{
    Properties
    {
        _Color ("Colour", Color) = (0.92, 0.95, 1, 0.6)
        _Rim ("Rim", Range(0, 1)) = 0.45
        _FadeOrigin ("Fade origin", Vector) = (0, 0, 0, 0)
        _FadeDir ("Fade direction", Vector) = (0, 0, 0, 0)
        _FadeLength ("Fade length", Float) = 1
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    float4 _FadeOrigin, _FadeDir;
    float _FadeLength;
    float ArmFade(float3 wp)
    {
        float d = dot(wp - _FadeOrigin.xyz, _FadeDir.xyz);           // distance past the wrist along the arm
        return 1 - smoothstep(0, max(1e-4, _FadeLength), d);
    }
    ENDCG
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct v2f { float4 pos : SV_POSITION; float3 wp : TEXCOORD0; };
            v2f vert(float4 v : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v);
                o.wp = mul(unity_ObjectToWorld, v).xyz;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { clip(ArmFade(i.wp) - 0.5); return 0; }
            ENDCG
        }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 _Color;
            float _Rim;
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 wp : TEXCOORD1; };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.n = UnityObjectToWorldNormal(v.normal);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float fade = ArmFade(i.wp);
                clip(fade - 0.002);
                float3 n = normalize(i.n);
                float3 v = normalize(_WorldSpaceCameraPos - i.wp);
                // only front faces are drawn: a normal pointing away (the mirrored right-hand model) is flipped
                n = dot(n, v) < 0 ? -n : n;
                float3 l = normalize(v + float3(0, 0.6, 0));
                float d = saturate(dot(n, l));
                float rim = pow(1 - saturate(dot(n, v)), 2.5);
                float3 c = _Color.rgb * (0.35 + 0.65 * d) + rim * _Rim;
                return fixed4(c, saturate(_Color.a + rim * 0.35) * fade);
            }
            ENDCG
        }
    }
}
