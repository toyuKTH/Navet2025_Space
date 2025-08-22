using UnityEngine;

public class EarthIceController : MonoBehaviour
{
    [Header("材质设置")]
    public Material earthMaterial; // 拖入你的地球材质
    
    [Header("自动冰冻设置")]
    [SerializeField] private bool autoFreeze = true;
    [SerializeField] private float freezeSpeed = 0.1f; // 冰冻速度（每秒）
    [SerializeField] private float maxFreeze = 1.0f; // 最大冰冻程度
    
    [Header("手动控制")]
    [Range(0f, 1f)]
    [SerializeField] private float manualFreezeLevel = 0f;
    
    [Header("当前状态")]
    [SerializeField] private float currentFreezeLevel = 0f;
    
    [Header("冰冻颜色设置")]
    [SerializeField] private Color iceColor = new Color(0.8f, 0.9f, 1.0f, 1.0f);
    [SerializeField] private Color snowColor = new Color(0.95f, 0.95f, 1.0f, 1.0f);
    
    // 私有变量
    private float targetFreezeLevel = 0f;
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
            currentFreezeLevel = 0f;
            earthMaterial.SetFloat("_FreezeLevel", currentFreezeLevel);
            earthMaterial.SetColor("_IceColor", iceColor);
            earthMaterial.SetColor("_SnowColor", snowColor);
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
        
        // 自动冰冻
        if (autoFreeze && !isPaused)
        {
            AutoFreeze();
        }
        
        // 手动控制优先级更高
        if (!autoFreeze)
        {
            targetFreezeLevel = manualFreezeLevel;
        }
        
        // 平滑过渡到目标值
        currentFreezeLevel = Mathf.MoveTowards(currentFreezeLevel, targetFreezeLevel, Time.deltaTime * freezeSpeed * 2);
        
        // 应用到材质
        earthMaterial.SetFloat("_FreezeLevel", currentFreezeLevel);
        earthMaterial.SetColor("_IceColor", iceColor);
        earthMaterial.SetColor("_SnowColor", snowColor);
        
        // 更新Inspector显示
        manualFreezeLevel = currentFreezeLevel;
    }
    
    void HandleInput()
    {
        // 空格键：暂停/恢复自动冰冻
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isPaused = !isPaused;
            Debug.Log($"自动冰冻 {(isPaused ? "暂停" : "恢复")}");
        }
        
        // R键：重置到原始状态
        if (Input.GetKeyDown(KeyCode.R))
        {
            ResetEarth();
        }
        
        // F键：快速冰冻到最大值
        if (Input.GetKeyDown(KeyCode.F))
        {
            FastFreeze();
        }
        
        // 切换自动/手动模式
        if (Input.GetKeyDown(KeyCode.T))
        {
            autoFreeze = !autoFreeze;
            Debug.Log($"切换到 {(autoFreeze ? "自动" : "手动")} 模式");
        }
    }
    
    void AutoFreeze()
    {
        // 逐渐增加冰冻程度
        targetFreezeLevel += freezeSpeed * Time.deltaTime;
        targetFreezeLevel = Mathf.Clamp(targetFreezeLevel, 0f, maxFreeze);
    }
    
    // 公共方法供外部调用
    public void SetFreezeLevel(float level)
    {
        targetFreezeLevel = Mathf.Clamp01(level);
        autoFreeze = false; // 切换到手动模式
    }
    
    public void ResetEarth()
    {
        targetFreezeLevel = 0f;
        currentFreezeLevel = 0f;
        Debug.Log("地球已重置到原始状态");
    }
    
    public void FastFreeze()
    {
        targetFreezeLevel = maxFreeze;
        Debug.Log("快速冰冻到最大程度");
    }
    
    public void SetFreezeSpeed(float speed)
    {
        freezeSpeed = Mathf.Max(0f, speed);
    }
    
    public void PauseFreeze()
    {
        isPaused = true;
    }
    
    public void ResumeFreeze()
    {
        isPaused = false;
    }
    
    // 设置冰冻颜色
    public void SetIceColor(Color color)
    {
        iceColor = color;
    }
    
    public void SetSnowColor(Color color)
    {
        snowColor = color;
    }
    
    // 获取当前状态
    public float GetCurrentFreezeLevel()
    {
        return currentFreezeLevel;
    }
    
    public bool IsAutoMode()
    {
        return autoFreeze;
    }
    
    public bool IsPaused()
    {
        return isPaused;
    }
    
    // 调试信息
    void OnGUI()
    {
        if (Debug.isDebugBuild)
        {
            GUI.Label(new Rect(10, 10, 300, 20), $"冰冻程度: {currentFreezeLevel:F2}");
            GUI.Label(new Rect(10, 30, 300, 20), $"模式: {(autoFreeze ? "自动" : "手动")}");
            GUI.Label(new Rect(10, 50, 300, 20), $"状态: {(isPaused ? "暂停" : "运行")}");
            GUI.Label(new Rect(10, 70, 300, 20), "按键: 空格(暂停) R(重置) F(快速冰冻) T(切换模式)");
        }
    }
}