using UnityEngine;

public class EarthVisibilityController : MonoBehaviour
{
    [Header("Earth物体引用")]
    public GameObject mainEarth; // 主场景中的Earth GameObject
    
    [Header("面板切换监听")]
    public SimplePanelSwitcher panelSwitcher; // 面板切换器引用
    
    [Header("面板配置")]
    public GameObject planetGroupPanel; // 主星球组面板
    public GameObject[] hiddenEarthPanels; // 需要隐藏主Earth的面板（如Planet1Panel, Planet2Panel等）
    
    [Header("调试")]
    public bool showDebugLog = true;
    
    private GameObject currentPanel;
    private bool isEarthVisible = true;
    
    void Start()
    {
        // 初始化当前面板状态
        if (panelSwitcher != null)
        {
            currentPanel = panelSwitcher.current;
        }
        
        // 根据初始面板状态设置Earth显示
        UpdateEarthVisibility();
        
        if (showDebugLog)
            Debug.Log("[EarthController] Earth显示控制器初始化完成");
    }
    
    void Update()
    {
        // 检查面板是否发生变化
        if (panelSwitcher != null && panelSwitcher.current != currentPanel)
        {
            OnPanelChanged(panelSwitcher.current);
            currentPanel = panelSwitcher.current;
        }
    }
    
    /// <summary>
    /// 面板切换时的回调
    /// </summary>
    void OnPanelChanged(GameObject newPanel)
    {
        if (showDebugLog)
            Debug.Log($"[EarthController] 面板切换: {currentPanel?.name} -> {newPanel?.name}");
            
        UpdateEarthVisibility();
    }
    
    /// <summary>
    /// 更新Earth的显示状态
    /// </summary>
    void UpdateEarthVisibility()
    {
        if (mainEarth == null || panelSwitcher == null) return;
        
        bool shouldShowEarth = ShouldShowEarth(panelSwitcher.current);
        
        if (shouldShowEarth != isEarthVisible)
        {
            SetEarthVisibility(shouldShowEarth);
        }
    }
    
    /// <summary>
    /// 判断当前面板是否应该显示Earth
    /// </summary>
    bool ShouldShowEarth(GameObject currentPanel)
    {
        if (currentPanel == null) return true;
        
        // 如果是主星球组面板，显示Earth
        if (currentPanel == planetGroupPanel)
        {
            return true;
        }
        
        // 如果是隐藏Earth的面板，不显示Earth
        if (hiddenEarthPanels != null)
        {
            foreach (var panel in hiddenEarthPanels)
            {
                if (panel == currentPanel)
                {
                    return false;
                }
            }
        }
        
        // 默认显示Earth
        return true;
    }
    
    /// <summary>
    /// 设置Earth的显示状态
    /// </summary>
    void SetEarthVisibility(bool visible)
    {
        if (mainEarth != null)
        {
            mainEarth.SetActive(visible);
            isEarthVisible = visible;
            
            if (showDebugLog)
                Debug.Log($"[EarthController] {(visible ? "显示" : "隐藏")} 主场景Earth");
        }
    }
    
    /// <summary>
    /// 手动显示Earth
    /// </summary>
    public void ShowEarth()
    {
        SetEarthVisibility(true);
        if (showDebugLog)
            Debug.Log("[EarthController] 手动显示Earth");
    }
    
    /// <summary>
    /// 手动隐藏Earth
    /// </summary>
    public void HideEarth()
    {
        SetEarthVisibility(false);
        if (showDebugLog)
            Debug.Log("[EarthController] 手动隐藏Earth");
    }
    
    /// <summary>
    /// 切换Earth显示状态
    /// </summary>
    public void ToggleEarth()
    {
        SetEarthVisibility(!isEarthVisible);
        if (showDebugLog)
            Debug.Log("[EarthController] 切换Earth显示状态");
    }
    
    /// <summary>
    /// 获取当前Earth显示状态
    /// </summary>
    public bool IsEarthVisible()
    {
        return isEarthVisible;
    }
    
    /// <summary>
    /// 强制刷新Earth显示状态
    /// </summary>
    [ContextMenu("刷新Earth状态")]
    public void RefreshEarthState()
    {
        UpdateEarthVisibility();
        if (showDebugLog)
            Debug.Log("[EarthController] 已刷新Earth显示状态");
    }
    
    /// <summary>
    /// 测试隐藏Earth
    /// </summary>
    [ContextMenu("测试隐藏Earth")]
    void TestHideEarth()
    {
        HideEarth();
    }
    
    /// <summary>
    /// 测试显示Earth
    /// </summary>
    [ContextMenu("测试显示Earth")]
    void TestShowEarth()
    {
        ShowEarth();
    }
}