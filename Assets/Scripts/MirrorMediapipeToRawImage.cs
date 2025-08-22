using UnityEngine;
using UnityEngine.UI;

// 起别名，避免与 UnityEngine.Screen 冲突
using MP = Mediapipe.Unity;
using HolisticNS = Mediapipe.Unity.Sample.Holistic;

[RequireComponent(typeof(RawImage))]
public class MirrorMediapipeToRawImage : MonoBehaviour
{
    [Tooltip("场景中的 HolisticTrackingSolution 组件")]
    public HolisticNS.HolisticTrackingSolution solution;

    [Tooltip("要显示到的 RawImage，不填就用当前物体")]
    public RawImage targetRawImage;

    [Tooltip("是否拷贝原 Screen 的显示参数")]
    public bool copyScreenSettings = true;

    void Awake()
    {
        if (!targetRawImage) targetRawImage = GetComponent<RawImage>();
    }

    void Start()
    {
        // 1) 找到 Solution
        if (!solution)
            solution = FindObjectOfType<HolisticNS.HolisticTrackingSolution>(true);
        if (!solution)
        {
            Debug.LogError("[MirrorMediapipeToRawImage] 未找到 HolisticTrackingSolution");
            return;
        }

        // 2) 尝试不同方式找到原来的 Screen
        MP.Screen originalScreen = null;
        
        // 方法1: 通过反射访问 screen 字段（如果是私有的）
        try
        {
            var screenField = typeof(HolisticNS.HolisticTrackingSolution).GetField("screen", 
                System.Reflection.BindingFlags.NonPublic | 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.Instance);
            if (screenField != null)
            {
                originalScreen = screenField.GetValue(solution) as MP.Screen;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[MirrorMediapipeToRawImage] 反射访问 screen 失败: " + e.Message);
        }

        // 方法2: 直接在场景中查找
        if (!originalScreen)
            originalScreen = FindObjectOfType<MP.Screen>(true);

        // 方法3: 在 solution 的子对象中查找
        if (!originalScreen && solution.transform)
            originalScreen = solution.GetComponentInChildren<MP.Screen>(true);

        if (!originalScreen)
        {
            Debug.LogError("[MirrorMediapipeToRawImage] 未找到原始 Screen");
            return;
        }

        // 3) 确保自己这边也有 MP.Screen 组件
        var myScreen = targetRawImage.GetComponent<MP.Screen>();
        if (!myScreen) myScreen = targetRawImage.gameObject.AddComponent<MP.Screen>();

        // 4) 复用同一份 ImageSource（使用反射或公共属性）
        TrySetImageSource(myScreen, originalScreen);

        // 5) 可选：拷贝显示参数（使用反射）
        if (copyScreenSettings)
        {
            TryCopyScreenSettings(myScreen, originalScreen);
        }

        // 6) 请求重绘（使用反射或公共方法）
        TryRequestResize(myScreen);
    }

    void TrySetImageSource(MP.Screen target, MP.Screen source)
    {
        try
        {
            // 尝试直接访问
            var imageSourceField = typeof(MP.Screen).GetField("imageSource", 
                System.Reflection.BindingFlags.NonPublic | 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.Instance);
            
            if (imageSourceField != null)
            {
                var sourceImageSource = imageSourceField.GetValue(source);
                imageSourceField.SetValue(target, sourceImageSource);
                Debug.Log("[MirrorMediapipeToRawImage] 成功设置 ImageSource");
            }
            else
            {
                Debug.LogWarning("[MirrorMediapipeToRawImage] 找不到 imageSource 字段");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("[MirrorMediapipeToRawImage] 设置 ImageSource 失败: " + e.Message);
        }
    }

    void TryCopyScreenSettings(MP.Screen target, MP.Screen source)
    {
        try
        {
            var screenType = typeof(MP.Screen);
            var flags = System.Reflection.BindingFlags.NonPublic | 
                       System.Reflection.BindingFlags.Public | 
                       System.Reflection.BindingFlags.Instance;

            // 尝试拷贝各种属性
            string[] propertiesToCopy = { 
                "keepAspectRatio", "fit", "rotation", 
                "flipHorizontally", "flipVertically" 
            };

            foreach (string propName in propertiesToCopy)
            {
                var field = screenType.GetField(propName, flags);
                var property = screenType.GetProperty(propName, flags);
                
                if (field != null)
                {
                    var value = field.GetValue(source);
                    field.SetValue(target, value);
                    Debug.Log($"[MirrorMediapipeToRawImage] 拷贝字段 {propName}: {value}");
                }
                else if (property != null && property.CanRead && property.CanWrite)
                {
                    var value = property.GetValue(source);
                    property.SetValue(target, value);
                    Debug.Log($"[MirrorMediapipeToRawImage] 拷贝属性 {propName}: {value}");
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("[MirrorMediapipeToRawImage] 拷贝屏幕设置失败: " + e.Message);
        }
    }

    void TryRequestResize(MP.Screen screen)
    {
        try
        {
            var method = typeof(MP.Screen).GetMethod("RequestResize", 
                System.Reflection.BindingFlags.NonPublic | 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.Instance);
            
            if (method != null)
            {
                method.Invoke(screen, null);
                Debug.Log("[MirrorMediapipeToRawImage] 成功调用 RequestResize");
            }
            else
            {
                Debug.LogWarning("[MirrorMediapipeToRawImage] 找不到 RequestResize 方法");
                
                // 替代方案：手动触发重绘
                if (targetRawImage)
                {
                    targetRawImage.SetVerticesDirty();
                    targetRawImage.SetMaterialDirty();
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("[MirrorMediapipeToRawImage] RequestResize 调用失败: " + e.Message);
        }
    }
}