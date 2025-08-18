using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using UnityRect = UnityEngine.Rect;

public class PointLeft : MonoBehaviour
{
    [Header("内容切换设置")]
    public float gestureHoldTime = 0.8f; // 降低保持时间，更灵敏
    
    [Header("手势识别设置")]
    public float pointDirectionThreshold = 0.03f; // 降低方向阈值，更灵敏
    public float fingerExtendThreshold = 0.02f; // 降低伸直阈值
    public float otherFingerBendThreshold = 0.02f; // 其他手指弯曲检测
    
    [Header("调试信息")]
    public bool showDebugInfo = true;
    public bool showLandmarkInfo = true;  // 改为true，显示关键点信息
    public bool showDetailedAnalysis = true;  // 新增：显示详细分析
    
    private bool pointLeftDetected = false;
    private float pointLeftTimer = 0f;
    private bool hasTriggered = false;
    
    // 手势检测相关
    private bool hasHandData = false;
    private int frameCount = 0;
    
    // 内容切换器引用
    private ContentSwitcher contentSwitcher;
    
    // 手部关键点数据
    private Vector3[] currentLandmarks = new Vector3[21];
    private bool landmarksUpdated = false;
    
    void Start()
    {
        Debug.Log("Point Left手势识别器已启动");
        
        // 找到ContentSwitcher组件
        contentSwitcher = FindObjectOfType<ContentSwitcher>();
        if (contentSwitcher == null)
        {
            Debug.LogError("找不到ContentSwitcher组件！");
        }
        else
        {
            Debug.Log("成功连接到ContentSwitcher");
        }
        
        // 尝试连接到手势检测系统
        StartCoroutine(ConnectToHandDetection());
    }
    
    IEnumerator ConnectToHandDetection()
    {
        yield return new WaitForSeconds(2f); // 等待系统完全初始化
        
        // 尝试连接到Holistic系统
        TryConnectToHolisticSystem();
        
        // 如果回调连接失败，使用轮询方式
        InvokeRepeating("TryGetHandDataDirectly", 0.1f, 0.1f);
        
        // 启动手势分析循环
        InvokeRepeating("AnalyzeHandGesture", 0.1f, 0.1f);
    }
    
    void TryConnectToHolisticSystem()
    {
        var solution = GameObject.Find("Solution");
        if (solution != null)
        {
            Debug.Log("找到Solution对象，连接Holistic系统");
            
            var holisticSolution = solution.GetComponent<Mediapipe.Unity.Sample.Holistic.HolisticTrackingSolution>();
            if (holisticSolution != null)
            {
                Debug.Log("成功连接到HolisticTrackingSolution");
                TryRegisterCallbacks(holisticSolution);
            }
            else
            {
                Debug.LogError("找不到HolisticTrackingSolution组件");
            }
        }
    }
    
    void TryRegisterCallbacks(Mediapipe.Unity.Sample.Holistic.HolisticTrackingSolution holisticSolution)
    {
        try
        {
            // 通过反射获取graphRunner
            var graphRunnerField = holisticSolution.GetType().GetField("graphRunner", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (graphRunnerField != null)
            {
                var graphRunner = graphRunnerField.GetValue(holisticSolution) as Mediapipe.Unity.Sample.Holistic.HolisticTrackingGraph;
                if (graphRunner != null)
                {
                    Debug.Log("通过字段反射获取到HolisticTrackingGraph");
                    graphRunner.OnLeftHandLandmarksOutput += OnLeftHandLandmarksReceived;
                    graphRunner.OnRightHandLandmarksOutput += OnRightHandLandmarksReceived;
                    Debug.Log("✓ 成功注册手势回调函数");
                    return;
                }
            }
            
            Debug.LogWarning("反射方法失败，将使用轮询方式");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"注册回调时出错: {e.Message}");
        }
    }
    
    // 左手关键点回调
    void OnLeftHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs eventArgs)
    {
        // Debug.Log("👈 OnLeftHandLandmarksReceived 被调用！");
        var packet = eventArgs.packet;
        if (packet != null)
        {
            Debug.Log("👈 左手数据包不为空");
            var landmarks = packet.Get(NormalizedLandmarkList.Parser);
            if (landmarks != null && landmarks.Landmark.Count >= 21)
            {
                UpdateHandLandmarks(landmarks.Landmark);
                Debug.Log($"👈 成功处理左手真实数据: {landmarks.Landmark.Count}个点");
            }
        }
    }
    
    // 右手关键点回调
    void OnRightHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs eventArgs)
    {
        // Debug.Log("👉 OnRightHandLandmarksReceived 被调用！");
        var packet = eventArgs.packet;
        if (packet != null)
        {
            Debug.Log("👉 右手数据包不为空");
            var landmarks = packet.Get(NormalizedLandmarkList.Parser);
            if (landmarks != null && landmarks.Landmark.Count >= 21)
            {
                UpdateHandLandmarks(landmarks.Landmark);
                Debug.Log($"👉 成功处理右手真实数据: {landmarks.Landmark.Count}个点");
            }
        }
    }
    
    // 备用方案：直接轮询获取手势数据
    void TryGetHandDataDirectly()
    {
        // 降低测试数据生成频率，优先使用真实数据
        if (Random.Range(0f, 1f) < 0.01f) // 降低到1%概率
        {
            Debug.Log("⚠️ 生成备用测试数据（真实数据可能未连接）");
            GenerateTestHandData();
        }
    }
    
    // 生成测试手势数据
    void GenerateTestHandData()
    {
        // 模拟point left关键点数据
        for (int i = 0; i < 21; i++)
        {
            currentLandmarks[i] = new Vector3(
                Random.Range(0.4f, 0.6f),  // X
                Random.Range(0.4f, 0.6f),  // Y  
                Random.Range(-0.05f, 0.05f)  // Z
            );
        }
        
        // 设置明显的point left姿态
        currentLandmarks[0] = new Vector3(0.5f, 0.5f, 0f);   // 手腕
        currentLandmarks[5] = new Vector3(0.4f, 0.5f, 0f);   // 食指掌关节
        currentLandmarks[8] = new Vector3(0.2f, 0.5f, 0f);   // 食指尖（指向左侧）
        
        hasHandData = true;
        landmarksUpdated = true;
    }
    
    void Update()
    {
        frameCount++;
        
        // 保留键盘测试功能 - 按P键测试Point Left
        if (Input.GetKey(KeyCode.P))
        {
            pointLeftDetected = true;
        }
        
        // 检查手势计时
        if (pointLeftDetected)
        {
            pointLeftTimer += Time.deltaTime;
            
            if (pointLeftTimer >= gestureHoldTime && !hasTriggered)
            {
                TriggerContentSwitch();
            }
        }
        else
        {
            pointLeftTimer = 0f;
        }
    }
    
    void AnalyzeHandGesture()
    {
        // 检查当前面板，只在Target面板时检测Point Left
        if (contentSwitcher != null && !contentSwitcher.IsInTargetState())
        {
            // 不在Target面板，不检测Point Left
            if (!Input.GetKey(KeyCode.P))
            {
                pointLeftDetected = false;
            }
            return;
        }
        
        if (!landmarksUpdated || !hasHandData)
        {
            // 如果没有手部数据，重置检测状态
            if (!Input.GetKey(KeyCode.P))
            {
                pointLeftDetected = false;
            }
            return;
        }
        
        // 分析当前手部关键点
        bool isPointLeft = DetectPointLeftGesture();
        
        if (isPointLeft)
        {
            pointLeftDetected = true;
            Debug.Log("✓ 检测到Point Left手势！");
        }
        else if (!Input.GetKey(KeyCode.P))
        {
            pointLeftDetected = false;
        }
        
        landmarksUpdated = false; // 重置更新标志
    }
    
    bool DetectPointLeftGesture()
    {
        try
        {
            // 关键点位置
            Vector3 indexTip = currentLandmarks[8];       // 食指尖
            Vector3 indexMcp = currentLandmarks[5];       // 食指掌关节
            Vector3 indexPip = currentLandmarks[6];       // 食指第二关节
            Vector3 wrist = currentLandmarks[0];          // 手腕
            Vector3 middleTip = currentLandmarks[12];     // 中指尖
            Vector3 ringTip = currentLandmarks[16];       // 无名指尖
            Vector3 pinkyTip = currentLandmarks[20];      // 小指尖
            
            // 检查关键点是否有效
            if (indexTip == Vector3.zero || indexMcp == Vector3.zero)
            {
                return false;
            }
            
            // 核心检测1：食指明显指向左侧（需要确定正确方向）
            float xDiff_tip_mcp = indexTip.x - indexMcp.x;  // 食指尖相对掌关节的X位移
            float xDiff_tip_wrist = indexTip.x - wrist.x;   // 食指尖相对手腕的X位移

               // 临时：两种方向都试试，看哪个是对的
            bool pointingLeftVersion1 = xDiff_tip_mcp < -pointDirectionThreshold; // 当前版本
            bool pointingLeftVersion2 = xDiff_tip_mcp > pointDirectionThreshold;  // 相反版本
            
            if (showDetailedAnalysis)
            {
                Debug.Log($"=== 坐标系调试 - 确定正确方向 ===");
                Debug.Log($"食指尖: {indexTip} (X={indexTip.x:F3})");
                Debug.Log($"食指掌关节: {indexMcp} (X={indexMcp.x:F3})");
                Debug.Log($"手腕: {wrist} (X={wrist.x:F3})");
                Debug.Log($"当前计算的X位移:");
                Debug.Log($"  尖->掌: {xDiff_tip_mcp:F3} (食指尖X - 掌关节X)");
                Debug.Log($"  尖->腕: {xDiff_tip_wrist:F3} (食指尖X - 手腕X)");
                Debug.Log($"请做指向左侧和右侧的手势，观察X坐标变化规律");
                Debug.Log($"版本1检测(< -阈值): {pointingLeftVersion1}");
                Debug.Log($"版本2检测(> +阈值): {pointingLeftVersion2}");
            }
            
         
            
            // 修正：根据实际测试，应该是这个方向
            bool pointingLeft1 = xDiff_tip_mcp > pointDirectionThreshold;  // 食指尖X > 掌关节X = 指向左侧
            bool pointingLeft2 = xDiff_tip_wrist > pointDirectionThreshold; // 食指尖X > 手腕X = 指向左侧
            
            // 核心检测2：食指伸直（通过关节链检测）
            // 无论手掌手背，伸直的食指都应该有这个特征
            float indexSegment1 = Vector3.Distance(indexMcp, indexPip);
            float indexSegment2 = Vector3.Distance(indexPip, indexTip);
            float indexTotalLength = Vector3.Distance(indexMcp, indexTip);
            bool indexExtended = indexTotalLength > fingerExtendThreshold && 
                                indexSegment1 > 0.01f && indexSegment2 > 0.01f;
            
            // 核心检测3：食指是突出的手指（相对其他手指更远）
            float indexDistanceFromWrist = Vector3.Distance(indexTip, wrist);
            float middleDistanceFromWrist = Vector3.Distance(middleTip, wrist);
            float ringDistanceFromWrist = Vector3.Distance(ringTip, wrist);
            float pinkyDistanceFromWrist = Vector3.Distance(pinkyTip, wrist);
            
            // 食指应该比其他手指更突出（更远离手腕）
            bool indexProtrudes = (indexDistanceFromWrist > middleDistanceFromWrist + otherFingerBendThreshold) ||
                                 (indexDistanceFromWrist > ringDistanceFromWrist + otherFingerBendThreshold) ||
                                 (indexDistanceFromWrist > pinkyDistanceFromWrist + otherFingerBendThreshold);
            
            // 核心检测4：食指方向一致性（所有关节都指向左侧）
            bool mcpPointsLeft = indexMcp.x < indexPip.x; // 掌关节在第二关节左侧
            bool pipPointsLeft = indexPip.x < indexTip.x; // 第二关节在指尖左侧
            bool consistentDirection = mcpPointsLeft && pipPointsLeft;
            
            // 可选检测5：相对水平（但标准放宽）
            float yDiff_mcp_tip = Mathf.Abs(indexTip.y - indexMcp.y);
            float xDiff_mcp_tip = Mathf.Abs(indexTip.x - indexMcp.x);
            bool relativelyHorizontal = yDiff_mcp_tip < xDiff_mcp_tip; // Y变化小于X变化
            
            if (showDetailedAnalysis)
            {
                Debug.Log($"方向检测 (修正后的左侧检测):");
                Debug.Log($"  X位移(尖->掌): {xDiff_tip_mcp:F3} < -{pointDirectionThreshold:F3} = {pointingLeft1}");
                Debug.Log($"  X位移(尖->腕): {xDiff_tip_wrist:F3} < -{pointDirectionThreshold:F3} = {pointingLeft2}");
                Debug.Log($"伸直检测:");
                Debug.Log($"  食指总长: {indexTotalLength:F3}, 分段1: {indexSegment1:F3}, 分段2: {indexSegment2:F3}");
                Debug.Log($"  食指伸直: {indexExtended}");
                Debug.Log($"突出检测:");
                Debug.Log($"  食指距手腕: {indexDistanceFromWrist:F3}");
                Debug.Log($"  中指距手腕: {middleDistanceFromWrist:F3}");
                Debug.Log($"  食指突出: {indexProtrudes}");
                Debug.Log($"方向一致性 (关节链指向左侧):");
                Debug.Log($"  掌关节->第二关节: {mcpPointsLeft} (掌关节X:{indexMcp.x:F3} > 第二关节X:{indexPip.x:F3})");
                Debug.Log($"  第二关节->指尖: {pipPointsLeft} (第二关节X:{indexPip.x:F3} > 指尖X:{indexTip.x:F3})");
                Debug.Log($"  整体一致性: {consistentDirection}");
                Debug.Log($"相对水平:");
                Debug.Log($"  Y变化: {yDiff_mcp_tip:F3}, X变化: {xDiff_mcp_tip:F3}, 相对水平: {relativelyHorizontal}");
            }
            
            // 渐进式判断（从严格到宽松）
            bool strictMode = pointingLeft1 && pointingLeft2 && indexExtended && indexProtrudes && consistentDirection && relativelyHorizontal;
            bool standardMode = pointingLeft1 && indexExtended && consistentDirection;
            bool basicMode = pointingLeft1 && indexExtended;
            bool emergencyMode = pointingLeft1; // 最宽松：只要指向左就行
            
            // 优先使用严格模式，逐级降低要求
            bool result = strictMode || standardMode || basicMode || emergencyMode;
            
            if (showDetailedAnalysis)
            {
                Debug.Log($"判断模式 (修正后的左侧检测):");
                Debug.Log($"  严格模式: {strictMode}");
                Debug.Log($"  标准模式: {standardMode}");
                Debug.Log($"  基础模式: {basicMode}");
                Debug.Log($"  应急模式: {emergencyMode}");
                Debug.Log($"最终结果: {result}");
                Debug.Log($"========================");
            }
            
            return result;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Point Left手势分析出错: {e.Message}");
            return false;
        }
    }
    
    // 重载方法：接受NormalizedLandmark列表（MediaPipe格式）
    public void UpdateHandLandmarks(Google.Protobuf.Collections.RepeatedField<NormalizedLandmark> landmarks)
    {
        if (landmarks != null && landmarks.Count >= 21)
        {
            for (int i = 0; i < 21; i++)
            {
                var landmark = landmarks[i];
                currentLandmarks[i] = new Vector3(landmark.X, landmark.Y, landmark.Z);
            }
            hasHandData = true;
            landmarksUpdated = true;
            
            Debug.Log($"✓ Point Left接收到真实MediaPipe数据：{landmarks.Count}个关键点");
            Debug.Log($"食指尖真实位置: {currentLandmarks[8]}");
            Debug.Log($"食指掌关节真实位置: {currentLandmarks[5]}");
        }
        else
        {
            hasHandData = false;
            Debug.Log("✗ Point Left手部数据不足，设置hasHandData=false");
        }
    }
    
    void TriggerContentSwitch()
    {
        hasTriggered = true;
        Debug.Log("Point Left手势确认，返回上一个界面！");
        
        if (contentSwitcher != null)
        {
            // 调用ContentSwitcher的返回方法
            contentSwitcher.GoBack(); // 假设你的ContentSwitcher有这个方法
        }
        else
        {
            Debug.LogError("ContentSwitcher未找到！");
        }
        
        // 3秒后重置
        Invoke("ResetTrigger", 3f);
    }
    
    void ResetTrigger()
    {
        hasTriggered = false;
        pointLeftDetected = false;
        Debug.Log("重置Point Left手势检测，可以再次触发");
    }
    
    void OnGUI()
    {
        if (showDebugInfo)
        {
            GUILayout.BeginArea(new UnityRect(370, 100, 400, 400)); // 增加高度
            GUILayout.Label("=== Point Left手势识别状态 ===");
            
            // 显示当前面板状态
            if (contentSwitcher != null)
            {
                bool isTargetState = contentSwitcher.IsInTargetState();
                GUILayout.Label($"当前面板: {(isTargetState ? "Target" : "其他")}");
                GUILayout.Label($"检测启用: {(isTargetState ? "是" : "否")}");
            }
            else
            {
                GUILayout.Label("ContentSwitcher: 未连接");
            }
            
            GUILayout.Label($"Point Left: {(pointLeftDetected ? "检测中" : "未检测")}");
            GUILayout.Label($"计时器: {pointLeftTimer:F1}s / {gestureHoldTime}s");
            GUILayout.Label($"已触发: {(hasTriggered ? "是" : "否")}");
            GUILayout.Label($"手部数据: {(hasHandData ? "有数据" : "无数据")}");
            GUILayout.Label($"数据更新: {(landmarksUpdated ? "是" : "否")}");
            
            GUILayout.Label("按住P键测试 或 做真实Point Left手势");
            GUILayout.Label("(仅在Target面板时检测)");
            
            if (hasHandData && showLandmarkInfo)
            {
                GUILayout.Label("--- 实时手部关键点 ---");
                GUILayout.Label($"食指尖: {currentLandmarks[8]:F3}");
                GUILayout.Label($"食指掌关节: {currentLandmarks[5]:F3}");
                GUILayout.Label($"手腕: {currentLandmarks[0]:F3}");
                
                // 显示简化的计算结果
                if (currentLandmarks[8] != Vector3.zero)
                {
                    bool pointingLeft = currentLandmarks[8].x < currentLandmarks[5].x;
                    float indexLength = Vector3.Distance(currentLandmarks[8], currentLandmarks[5]);
                    GUILayout.Label($"指向左侧: {pointingLeft}");
                    GUILayout.Label($"食指长度: {indexLength:F3}");
                }
            }
            
            GUILayout.EndArea();
        }
    }
}