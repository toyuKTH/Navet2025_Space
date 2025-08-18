using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
public class ForceSizeKeeper : MonoBehaviour
{
    [Header("强制保持的尺寸")]
    public Vector2 forcedSize = new Vector2(500, 500);
    
    [Header("镜像修复选项")]
    public bool fixUVRect = true;
    public bool flipHorizontally = false;  // 水平翻转
    public bool flipVertically = false;    // 垂直翻转
    
    [Header("调试选项")]
    public bool showDebugInfo = true;
    
    private RawImage rawImage;
    private RectTransform rectTransform;
    private Rect originalUVRect;
    
    void Awake()
    {
        rawImage = GetComponent<RawImage>();
        rectTransform = GetComponent<RectTransform>();
        
        // 记录原始UV Rect
        originalUVRect = rawImage.uvRect;
    }
    
    void Start()
    {
        ApplySettings();
    }
    
    void LateUpdate()
    {
        // 每帧强制保持设定的尺寸
        if (rectTransform.sizeDelta != forcedSize)
        {
            if (showDebugInfo)
                Debug.Log($"检测到尺寸被修改: {rectTransform.sizeDelta} -> 强制改回: {forcedSize}");
            rectTransform.sizeDelta = forcedSize;
        }
        
        // 修复UV Rect
        if (fixUVRect)
        {
            Rect targetUVRect = GetCorrectedUVRect();
            if (rawImage.uvRect != targetUVRect)
            {
                if (showDebugInfo)
                    Debug.Log($"修复UV Rect: {rawImage.uvRect} -> {targetUVRect}");
                rawImage.uvRect = targetUVRect;
            }
        }
    }
    
    void ApplySettings()
    {
        // 设置尺寸
        rectTransform.sizeDelta = forcedSize;
        
        // 修复UV Rect
        if (fixUVRect)
        {
            rawImage.uvRect = GetCorrectedUVRect();
        }
        
        if (showDebugInfo)
        {
            Debug.Log($"应用强制设置: 尺寸={forcedSize}, UV Rect={GetCorrectedUVRect()}, " +
                     $"水平翻转={flipHorizontally}, 垂直翻转={flipVertically}");
        }
    }
    
    // 根据翻转设置计算正确的UV Rect
    private Rect GetCorrectedUVRect()
    {
        float x = flipHorizontally ? 1f : 0f;
        float y = flipVertically ? 1f : 0f;
        float width = flipHorizontally ? -1f : 1f;
        float height = flipVertically ? -1f : 1f;
        
        return new Rect(x, y, width, height);
    }
    
    // Inspector中修改参数时立即应用
    void OnValidate()
    {
        if (Application.isPlaying && rawImage != null)
        {
            ApplySettings();
        }
    }
    
    // 提供公共方法供外部调用
    public void SetForcedSize(Vector2 newSize)
    {
        forcedSize = newSize;
        ApplySettings();
    }
    
    // 快速修复镜像的方法
    public void FixMirrorIssue()
    {
        // 尝试自动检测和修复镜像问题
        if (rawImage.texture != null)
        {
            // 检查纹理导入设置
            var texture = rawImage.texture as Texture2D;
            if (texture != null)
            {
                if (showDebugInfo)
                {
                    Debug.Log($"纹理信息: 名称={texture.name}, 尺寸={texture.width}x{texture.height}");
                }
            }
        }
        
        // 常见的镜像问题是垂直翻转
        flipVertically = !flipVertically;
        ApplySettings();
        
        if (showDebugInfo)
            Debug.Log("已尝试修复镜像问题，如果还有问题请手动调整翻转选项");
    }
    
    // 重置到原始UV Rect
    public void ResetToOriginalUVRect()
    {
        rawImage.uvRect = originalUVRect;
        fixUVRect = false;
        
        if (showDebugInfo)
            Debug.Log($"重置到原始UV Rect: {originalUVRect}");
    }
    
    // 在Inspector中添加按钮（需要自定义Editor或使用[ContextMenu]）
    [ContextMenu("修复镜像问题")]
    public void ContextMenuFixMirror()
    {
        FixMirrorIssue();
    }
    
    [ContextMenu("重置UV Rect")]
    public void ContextMenuResetUV()
    {
        ResetToOriginalUVRect();
    }
}