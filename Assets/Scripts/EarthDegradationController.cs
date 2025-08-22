using UnityEngine;

public class EarthDegradationController : MonoBehaviour
{
    [Header("材质设置")]
    public Material earthMaterial; // 拖入你的地球材质
    
    [Header("自动恶化设置")]
    [SerializeField] private bool autoDegrade = true;
    [SerializeField] private float degradeSpeed = 0.1f; // 恶化速度（每秒）
    [SerializeField] private float maxDegradation = 1.0f; // 最大恶化程度
    
    [Header("手动控制")]
    [Range(0f, 1f)]
    [SerializeField] private float manualDryLevel = 0f;
    
    [Header("当前状态")]
    [SerializeField] private float currentDryLevel = 0f;
    
    // 私有变量
    private float targetDryLevel = 0f;
    private bool isPaused = false;
    
    void Start()
    {
        // 如果没有指定材质，尝试从当前对象获取
        if (earthMaterial == null)
        {
            Renderer renderer = GetComponent<Renderer>();
            if (renderer != null)
            {
                earthMaterial = renderer.material;
            }
        }
        
        // 初始化
        if (earthMaterial != null)
        {
            currentDryLevel = 0f;
            earthMaterial.SetFloat("_DryLevel", currentDryLevel);
        }
        else
        {
            Debug.LogError("Earth Material 未设置！请在Inspector中拖入地球材质。");
        }
    }
    
    void Update()
    {
        if (earthMaterial == null) return;
        
        // 处理键盘输入
        HandleInput();
        
        // 自动恶化
        if (autoDegrade && !isPaused)
        {
            AutoDegrade();
        }
        
        // 手动控制优先级更高
        if (!autoDegrade)
        {
            targetDryLevel = manualDryLevel;
        }
        
        // 平滑过渡到目标值
        currentDryLevel = Mathf.MoveTowards(currentDryLevel, targetDryLevel, Time.deltaTime * degradeSpeed * 2);
        
        // 应用到材质
        earthMaterial.SetFloat("_DryLevel", currentDryLevel);
        
        // 更新Inspector显示
        manualDryLevel = currentDryLevel;
    }
    
    void HandleInput()
    {
        // 空格键：暂停/恢复自动恶化
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isPaused = !isPaused;
            Debug.Log($"自动恶化 {(isPaused ? "暂停" : "恢复")}");
        }
        
        // R键：重置到原始状态
        if (Input.GetKeyDown(KeyCode.R))
        {
            ResetEarth();
        }
        
        // F键：快速恶化到最大值
        if (Input.GetKeyDown(KeyCode.F))
        {
            FastDegrade();
        }
        
        // 切换自动/手动模式
        if (Input.GetKeyDown(KeyCode.T))
        {
            autoDegrade = !autoDegrade;
            Debug.Log($"切换到 {(autoDegrade ? "自动" : "手动")} 模式");
        }
    }
    
    void AutoDegrade()
    {
        // 逐渐增加恶化程度
        targetDryLevel += degradeSpeed * Time.deltaTime;
        targetDryLevel = Mathf.Clamp(targetDryLevel, 0f, maxDegradation);
        
        // 当达到最大恶化时可以选择重新开始
        if (targetDryLevel >= maxDegradation)
        {
            // 可以在这里添加重新开始的逻辑
            // 比如延迟几秒后重置
        }
    }
    
    // 公共方法供外部调用
    public void SetDegradationLevel(float level)
    {
        targetDryLevel = Mathf.Clamp01(level);
        autoDegrade = false; // 切换到手动模式
    }
    
    public void ResetEarth()
    {
        targetDryLevel = 0f;
        currentDryLevel = 0f;
        Debug.Log("地球已重置到原始状态");
    }
    
    public void FastDegrade()
    {
        targetDryLevel = maxDegradation;
        Debug.Log("快速恶化到最大程度");
    }
    
    public void SetDegradeSpeed(float speed)
    {
        degradeSpeed = Mathf.Max(0f, speed);
    }
    
    public void PauseDegrade()
    {
        isPaused = true;
    }
    
    public void ResumeDegrade()
    {
        isPaused = false;
    }
    
    // 获取当前状态
    public float GetCurrentDegradationLevel()
    {
        return currentDryLevel;
    }
    
    public bool IsAutoMode()
    {
        return autoDegrade;
    }
    
    public bool IsPaused()
    {
        return isPaused;
    }
    
    // 在Inspector中显示帮助信息
    void OnDrawGizmos()
    {
        // 可以在这里绘制一些调试信息
    }
    
    // 调试信息
    void OnGUI()
    {
        if (Debug.isDebugBuild)
        {
            GUI.Label(new Rect(10, 10, 300, 20), $"恶化程度: {currentDryLevel:F2}");
            GUI.Label(new Rect(10, 30, 300, 20), $"模式: {(autoDegrade ? "自动" : "手动")}");
            GUI.Label(new Rect(10, 50, 300, 20), $"状态: {(isPaused ? "暂停" : "运行")}");
            GUI.Label(new Rect(10, 70, 300, 20), "按键: 空格(暂停) R(重置) F(快速恶化) T(切换模式)");
        }
    }
}