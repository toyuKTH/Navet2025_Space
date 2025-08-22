Shader "Custom/EarthDegradation" {
    Properties {
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _DryLevel ("Dry Level", Range(0,1)) = 0
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _EmissionColor ("Emission Color", Color) = (0,0,0,1)
    }
    
    SubShader {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _NormalMap;
        half _DryLevel;
        half _Glossiness;
        half _Metallic;
        fixed4 _EmissionColor;

        struct Input {
            float2 uv_MainTex;
            float2 uv_NormalMap;
        };

        void surf (Input IN, inout SurfaceOutputStandard o) {
            // 获取原始颜色
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex);
            
            // 分析原始颜色特征
            float brightness = dot(c.rgb, fixed3(0.299, 0.587, 0.114));
            float blueness = c.b - max(c.r, c.g); // 检测蓝色强度（海洋）
            float greenness = c.g - max(c.r, c.b); // 检测绿色强度（植被）
            
            // 更激进的干旱效果
            fixed3 dryColor = c.rgb;
            
            // 海洋变化：蓝色区域变成深棕色泥土
            if (blueness > 0.1) {
                // 海洋区域：深蓝变深棕
                dryColor = lerp(c.rgb, fixed3(0.4, 0.25, 0.1), saturate(blueness * 3));
            }
            
            // 植被变化：绿色区域变成枯黄色
            if (greenness > 0.1) {
                // 植被区域：绿色变枯黄
                dryColor = lerp(dryColor, fixed3(0.8, 0.6, 0.2), saturate(greenness * 3));
            }
            
            // 整体色调偏移：增加暖色调，减少冷色调
            dryColor.r *= 1.6; // 增强红色
            dryColor.g *= 1.2; // 适量增强绿色  
            dryColor.b *= 0.3; // 大幅减少蓝色
            
            // 增加对比度让效果更明显
            dryColor = saturate(dryColor);
            dryColor = pow(dryColor, 0.8); // 增加对比度
            
            // 混合原始颜色和干旱颜色
            c.rgb = lerp(c.rgb, dryColor, _DryLevel);
            
            // 添加法线贴图支持
            o.Normal = UnpackNormal(tex2D(_NormalMap, IN.uv_NormalMap));
            
            o.Albedo = c.rgb;
            o.Metallic = _Metallic * (1.0 - _DryLevel * 0.5); // 干旱时降低金属感
            o.Smoothness = _Glossiness * (1.0 - _DryLevel * 0.7); // 干旱时大幅降低光滑度
            
            // 添加微弱的发光效果模拟热辐射
            o.Emission = _EmissionColor.rgb + fixed3(0.1, 0.05, 0.0) * _DryLevel;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}