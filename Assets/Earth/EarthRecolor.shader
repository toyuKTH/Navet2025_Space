Shader "Custom/EarthRecolor"
{
    Properties{
        _MainTex("Albedo", 2D) = "white" {}
        _Blend("Depletion", Range(0,1)) = 0
        _TintStrength("Tint Strength", Range(0,1)) = 1
        _Threshold("Land/Water Threshold", Range(-0.2,0.2)) = 0

        _LandA("Land Healthy", Color)  = (0.20, 0.85, 0.25, 1)
        _LandB("Land Depleted", Color) = (1.00, 0.85, 0.00, 1)
        _WaterA("Water Healthy", Color)  = (0.20, 0.55, 1.00, 1)
        _WaterB("Water Depleted", Color) = (0.55, 0.25, 0.80, 1)

        _Glossiness("Smoothness", Range(0,1)) = 0.5
        _Metallic("Metallic", Range(0,1)) = 0
    }
    SubShader{
        Tags{ "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        half _Glossiness, _Metallic;
        half _Blend, _TintStrength, _Threshold;
        fixed4 _LandA, _LandB, _WaterA, _WaterB;

        struct Input { float2 uv_MainTex; };

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex);

            // 用“G - B”的符号把像素分成陆地/海洋（你的贴图非常适合）
            half isLand = step(_Threshold, c.g - c.b); // 1=陆地, 0=海洋

            fixed3 landTarget  = lerp(_LandA.rgb,  _LandB.rgb,  _Blend);
            fixed3 waterTarget = lerp(_WaterA.rgb, _WaterB.rgb, _Blend);
            fixed3 target = lerp(waterTarget, landTarget, isLand);

            fixed3 tinted = lerp(c.rgb, target, _TintStrength);

            o.Albedo     = tinted;
            o.Metallic   = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha      = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
