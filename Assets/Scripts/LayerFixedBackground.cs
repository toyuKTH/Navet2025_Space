using UnityEngine;
using UnityEngine.UI;
using Mediapipe.Unity.Sample.Holistic;
using Mediapipe.Unity;

public class LayerFixedBackground : MonoBehaviour
{
    [Header("背景设置")]
    public Texture2D customBackground;
    public bool enableBackgroundReplacement = true;
    
    [Header("调试")]
    public bool showDebug = true;
    
    private HolisticTrackingSolution solution;
    private RawImage cameraDisplay;
    private GameObject backgroundObject;
    private bool isSetup = false;
    
    void Start()
    {
        Debug.Log("✓ 层级修复版背景替换脚本已启动！");
        SetupBackgroundReplacement();
    }
    
    void SetupBackgroundReplacement()
    {
        // 查找MediaPipe组件
        solution = FindObjectOfType<HolisticTrackingSolution>();
        if (solution != null)
        {
            Debug.Log("✓ 找到HolisticTrackingSolution");
            solution.enableSegmentation = true;
            solution.smoothSegmentation = true;
        }
        
        // 查找摄像头显示组件
        FindCameraDisplay();
        
        isSetup = true;
    }
    
    void FindCameraDisplay()
    {
        // 查找Annotatable Screen
        GameObject screenObj = GameObject.Find("Annotatable Screen");
        if (screenObj != null)
        {
            cameraDisplay = screenObj.GetComponent<RawImage>();
            if (cameraDisplay != null)
            {
                Debug.Log($"✓ 找到摄像头显示：{screenObj.name}");
                Debug.Log($"摄像头Canvas层级：{cameraDisplay.canvas?.sortingOrder}");
                Debug.Log($"摄像头在Canvas中的层级：{cameraDisplay.transform.GetSiblingIndex()}");
            }
        }
    }
    
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.B))
        {
            enableBackgroundReplacement = !enableBackgroundReplacement;
            Debug.Log($"背景替换已{(enableBackgroundReplacement ? "启用" : "禁用")}");
            
            if (enableBackgroundReplacement)
            {
                CreateSmartBackground();
            }
            else
            {
                RemoveBackground();
            }
        }
        
        // 按C键显示摄像头信息
        if (Input.GetKeyDown(KeyCode.C))
        {
            ShowCameraInfo();
        }
        
        // 按N键尝试不同的背景策略
        if (Input.GetKeyDown(KeyCode.N))
        {
            TryDifferentStrategy();
        }
    }
    
    void CreateSmartBackground()
    {
        if (customBackground == null)
        {
            Debug.LogWarning("没有设置自定义背景图片！");
            return;
        }
        
        RemoveBackground(); // 先移除之前的背景
        
        // 策略1：在摄像头Canvas的同一层级创建背景
        if (cameraDisplay != null && cameraDisplay.canvas != null)
        {
            CreateBackgroundInSameCanvas();
        }
        else
        {
            // 策略2：创建独立的高优先级Canvas
            CreateHighPriorityCanvas();
        }
    }
    
    void CreateBackgroundInSameCanvas()
    {
        Canvas targetCanvas = cameraDisplay.canvas;
        
        // 创建背景GameObject
        backgroundObject = new GameObject("SmartCustomBackground");
        backgroundObject.transform.SetParent(targetCanvas.transform, false);
        
        // 重要：设置为第一个子对象（最底层）
        backgroundObject.transform.SetAsFirstSibling();
        
        // 添加Image组件
        Image backgroundImage = backgroundObject.AddComponent<Image>();
        backgroundImage.sprite = Sprite.Create(customBackground, 
            new Rect(0, 0, customBackground.width, customBackground.height), 
            Vector2.one * 0.5f);
        
        // 设置为全屏
        RectTransform rectTransform = backgroundObject.GetComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.sizeDelta = Vector2.zero;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;
        
        // 设置渲染顺序
        backgroundImage.raycastTarget = false; // 不阻挡射线检测
        
        Debug.Log($"✓ 在摄像头Canvas中创建背景，层级：{backgroundObject.transform.GetSiblingIndex()}");
        
        // 确保摄像头在背景之上
        if (cameraDisplay != null)
        {
            cameraDisplay.transform.SetAsLastSibling();
            Debug.Log($"摄像头新层级：{cameraDisplay.transform.GetSiblingIndex()}");
        }
    }
    
    void CreateHighPriorityCanvas()
    {
        // 创建新的Canvas作为背景层
        GameObject canvasObj = new GameObject("BackgroundCanvas");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -100; // 设置为最低优先级
        
        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        
        canvasObj.AddComponent<GraphicRaycaster>();
        
        // 创建背景Image
        backgroundObject = new GameObject("CustomBackground");
        backgroundObject.transform.SetParent(canvas.transform, false);
        
        Image backgroundImage = backgroundObject.AddComponent<Image>();
        backgroundImage.sprite = Sprite.Create(customBackground, 
            new Rect(0, 0, customBackground.width, customBackground.height), 
            Vector2.one * 0.5f);
        
        // 设置为全屏
        RectTransform rectTransform = backgroundObject.GetComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.sizeDelta = Vector2.zero;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;
        
        backgroundImage.raycastTarget = false;
        
        Debug.Log($"✓ 创建独立背景Canvas，优先级：{canvas.sortingOrder}");
    }
    
    void TryDifferentStrategy()
    {
        if (customBackground == null) return;
        
        RemoveBackground();
        
        // 尝试直接替换摄像头纹理
        if (cameraDisplay != null)
        {
            // 保存原始纹理
            Texture originalTexture = cameraDisplay.texture;
            
            // 临时替换为背景图片
            cameraDisplay.texture = customBackground;
            
            Debug.Log("✓ 直接替换摄像头纹理");
            
            // 3秒后恢复
            Invoke("RestoreCameraTexture", 3f);
        }
    }
    
    void RestoreCameraTexture()
    {
        if (cameraDisplay != null)
        {
            // 这里需要获取原始的摄像头纹理，但这比较复杂
            Debug.Log("需要恢复摄像头纹理");
        }
    }
    
    void ShowCameraInfo()
    {
        if (cameraDisplay != null)
        {
            Debug.Log("=== 摄像头信息 ===");
            Debug.Log($"对象名称：{cameraDisplay.gameObject.name}");
            Debug.Log($"Canvas：{cameraDisplay.canvas?.name}");
            Debug.Log($"Canvas排序顺序：{cameraDisplay.canvas?.sortingOrder}");
            Debug.Log($"在Canvas中的层级：{cameraDisplay.transform.GetSiblingIndex()}");
            Debug.Log($"当前纹理：{cameraDisplay.texture?.name}");
            Debug.Log($"纹理尺寸：{cameraDisplay.texture?.width}x{cameraDisplay.texture?.height}");
        }
        
        // 显示所有Canvas信息
        Canvas[] allCanvas = FindObjectsOfType<Canvas>();
        Debug.Log($"场景中共有{allCanvas.Length}个Canvas：");
        foreach (var canvas in allCanvas)
        {
            Debug.Log($"  {canvas.name} - 排序顺序：{canvas.sortingOrder}");
        }
    }
    
    void RemoveBackground()
    {
        if (backgroundObject != null)
        {
            if (backgroundObject.transform.parent?.name == "BackgroundCanvas")
            {
                // 如果背景在独立Canvas中，删除整个Canvas
                DestroyImmediate(backgroundObject.transform.parent.gameObject);
            }
            else
            {
                // 否则只删除背景对象
                DestroyImmediate(backgroundObject);
            }
            backgroundObject = null;
            Debug.Log("✓ 已移除背景");
        }
    }
    
    void OnGUI()
    {
        if (showDebug)
        {
            GUILayout.BeginArea(new Rect(10, 10, 400, 250));
            GUILayout.Label("=== 层级修复版背景替换 ===");
            GUILayout.Label($"设置状态: {(isSetup ? "已完成" : "设置中...")}");
            GUILayout.Label($"背景替换: {(enableBackgroundReplacement ? "启用" : "禁用")}");
            GUILayout.Label($"自定义背景: {(customBackground != null ? customBackground.name : "未设置")}");
            GUILayout.Label($"摄像头显示: {(cameraDisplay != null ? cameraDisplay.name : "未找到")}");
            GUILayout.Label($"背景对象: {(backgroundObject != null ? "已创建" : "未创建")}");
            
            GUILayout.Label("");
            GUILayout.Label("控制键:");
            GUILayout.Label("B键 - 智能背景替换");
            GUILayout.Label("C键 - 显示摄像头信息");
            GUILayout.Label("N键 - 尝试不同策略");
            
            if (GUILayout.Button("创建智能背景"))
            {
                CreateSmartBackground();
            }
            
            if (GUILayout.Button("显示摄像头信息"))
            {
                ShowCameraInfo();
            }
            
            GUILayout.EndArea();
        }
    }
    
    void OnDestroy()
    {
        RemoveBackground();
    }
}