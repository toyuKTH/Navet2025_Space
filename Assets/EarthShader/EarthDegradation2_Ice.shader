Shader "Custom/EarthDegradation2_Ice" {
    Properties {
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _FreezeLevel ("Freeze Level", Range(0,1)) = 0
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _IceColor ("Ice Color", Color) = (0.8, 0.9, 1.0, 1.0)
        _SnowColor ("Snow Color", Color) = (0.95, 0.95, 1.0, 1.0)
    }
    
    SubShader {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _NormalMap;
        half _FreezeLevel;
        half _Glossiness;
        half _Metallic;
        fixed4 _EmissionColor;
        fixed4 _IceColor;
        fixed4 _SnowColor;

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
            float warmth = (c.r + c.g) - c.b; // 检测暖色调（陆地）
            
            // 冰冻效果颜色
            fixed3 frozenColor = c.rgb;
            
            // 海洋变化：蓝色变成冰蓝色
            if (blueness > 0.05) {
                // 海洋区域：深蓝变冰蓝
                frozenColor = lerp(c.rgb, _IceColor.rgb * 0.7, saturate(blueness * 4));
            }
            
            // 植被和陆地变化：所有区域变成雪白色
            if (greenness > 0.05 || warmth > 0.1) {
                // 陆地区域：变成雪白色
                frozenColor = lerp(frozenColor, _SnowColor.rgb, saturate((greenness + warmth) * 2));
            }
            
            // 整体去饱和化处理
            float desaturated = dot(frozenColor, fixed3(0.3, 0.6, 0.1));
            frozenColor = lerp(frozenColor, fixed3(desaturated, desaturated, desaturated), 0.6);
            
            // 增加冷色调偏移
            frozenColor.r *= 0.8; // 减少红色（暖色）
            frozenColor.g *= 0.9; // 稍微减少绿色
            frozenColor.b *= 1.3; // 增强蓝色（冷色）
            
            // 整体提亮（雪和冰的反射）
            frozenColor *= lerp(1.0, 1.4, _FreezeLevel);
            
            // 增加对比度
            frozenColor = saturate(frozenColor);
            frozenColor = pow(frozenColor, lerp(1.0, 0.7, _FreezeLevel)); // 冰冻时增加对比度
            
            // 混合原始颜色和冰冻颜色
            c.rgb = lerp(c.rgb, frozenColor, _FreezeLevel);
            
            // 添加法线贴图支持
            o.Normal = UnpackNormal(tex2D(_NormalMap, IN.uv_NormalMap));
            
            o.Albedo = c.rgb;
            
            // 冰冻时增加金属感和光滑度（冰的反射特性）
            o.Metallic = _Metallic + _FreezeLevel * 0.3;
            o.Smoothness = _Glossiness + _FreezeLevel * 0.5; // 冰面很光滑
            
            // 添加冷色调发光效果
            fixed3 iceGlow = fixed3(0.0, 0.05, 0.1) * _FreezeLevel;
            o.Emission = _EmissionColor.rgb + iceGlow;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}