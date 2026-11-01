// One-cell soft goal glow with a thin, optionally focused halo. Used by both board views.
Shader "Nectorial/GoalFocus"
{
    Properties
    {
        _Color ("Color", Color) = (0.4, 0.71, 0.83, 1)
        _Focus ("Focus", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Focus;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * 2.0 - 1.0;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float radius = length(i.uv);
                float falloff = 1.0 - smoothstep(0.0, 1.0, radius);
                float glow = 0.25 * falloff * falloff;
                float focus = saturate(_Focus);
                // At full focus the outer edge still stays inside the one-cell quad.
                float ringRadius = 0.78 * (1.0 + 0.2 * focus);
                float ring = (1.0 - smoothstep(0.012, 0.036, abs(radius - ringRadius))) * lerp(0.25, 0.4, focus);
                return fixed4(_Color.rgb, saturate(glow + ring) * _Color.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
