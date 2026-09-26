// Soft round contact shadow on a flat quad (UV 0..1). Alpha fades radially; no shadow maps.
Shader "Nectorial/Preview3DContactShadow"
{
    Properties
    {
        _Color ("Color", Color) = (0.16, 0.2, 0.22, 0.35)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "IgnoreProjector" = "True" }
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

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

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * 2 - 1;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float falloff = 1 - smoothstep(0.35, 1.0, length(i.uv));
                return fixed4(_Color.rgb, _Color.a * falloff);
            }
            ENDCG
        }
    }
    Fallback Off
}
