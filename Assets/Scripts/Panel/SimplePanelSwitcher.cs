using UnityEngine;

public class SimplePanelSwitcher : MonoBehaviour
{
    [Header("当前面板")]
    public GameObject current; // 当前激活的面板
    
    [Header("调试")]
    public bool showDebugLog = true;
    
    /// <summary>
    /// 切换到指定面板
    /// </summary>
    public void SwitchTo(GameObject targetPanel)
    {
        if (targetPanel == null)
        {
            if (showDebugLog)
                Debug.LogWarning("[SimplePanelSwitcher] 目标面板为空！");
            return;
        }
        
        if (current == targetPanel)
        {
            if (showDebugLog)
                Debug.Log("[SimplePanelSwitcher] 目标面板已经是当前面板");
            return;
        }
        
        // 隐藏当前面板
        if (current != null)
        {
            current.SetActive(false);
            if (showDebugLog)
                Debug.Log($"[SimplePanelSwitcher] 隐藏面板: {current.name}");
        }
        
        // 显示目标面板
        targetPanel.SetActive(true);
        current = targetPanel;
        
        if (showDebugLog)
            Debug.Log($"[SimplePanelSwitcher] 切换到面板: {targetPanel.name}");
    }
    
    /// <summary>
    /// 获取当前面板
    /// </summary>
    public GameObject GetCurrent()
    {
        return current;
    }
    
    /// <summary>
    /// 设置当前面板（不进行切换动作）
    /// </summary>
    public void SetCurrent(GameObject panel)
    {
        current = panel;
        if (showDebugLog)
            Debug.Log($"[SimplePanelSwitcher] 设置当前面板: {panel?.name}");
    }
}