// Flat-colored lambert with a fixed key light and a sky/ground ambient term, plus a restrained Blinn
// highlight and an optional lift on up-facing faces so bevels and tops read as solid pieces.
// Needs no scene lights, shadow maps, textures, or render pipeline packages.
Shader "Nectorial/Preview3DLit"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Specular ("Specular", Range(0, 0.5)) = 0
        _TopLift ("Top Face Lift", Range(0, 0.3)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            half _Specular;
            half _TopLift;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                // Key light from upper left, slightly toward the viewer.
                float3 lightDir = normalize(float3(-0.45, 0.85, -0.35));
                float diffuse = saturate(dot(n, lightDir));
                float sky = n.y * 0.5 + 0.5;
                // Up-facing surfaces land near their base color; vertical sides fall to about 70% for depth.
                float3 ambient = lerp(float3(0.46, 0.46, 0.45), float3(0.70, 0.70, 0.69), sky);
                float3 viewDir = normalize(UnityWorldSpaceViewDir(i.worldPos));
                float highlight = pow(saturate(dot(n, normalize(lightDir + viewDir))), 32) * _Specular;
                float top = saturate((n.y - 0.7) * 3.33) * _TopLift;
                return fixed4(saturate(_Color.rgb * (ambient + diffuse * 0.36) + highlight + top), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
