using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using UnityRect = UnityEngine.Rect;

public class ThumbUp : MonoBehaviour
{
    [Header("内容切换设置")]
    public float gestureHoldTime = 1.0f;

    [Header("手势识别设置")]
    public float thumbUpThreshold = 0.08f; // 拇指向上的最小高度差
    public float fingerHeightDifference = 0.04f;  // 拇指比其他手指高出的最小差值
    public float fingerBendThreshold = 0.03f;     // 手指弯曲的阈值
    public float maxThumbAngle = 45f;             // 拇指允许的最大角度偏差
    
    [Header("稳定性设置")]
    public int requiredStableFrames = 3;  // 需要连续几帧都检测到
    public float gestureResetTime = 1.5f;  // 手势重置时间
    
    [Header("检测模式")]
    public bool useStrictMode = false;    // 严格模式
    public bool useStandardMode = true;   // 标准模式（推荐）
    public bool allowRelaxedDetection = false;    // 是否允许宽松检测模式

    [Header("调试信息")]
    public bool showDebugInfo = true;
    public bool showLandmarkInfo = false;
    public bool showDetailedAnalysis = false;  // 显示详细分析信息

    private bool thumbsUpDetected = false;
    private float thumbsUpTimer = 0f;
    private bool hasTriggered = false;
    private int thumbsUpStableCount = 0;
    private float lastGestureTime = 0f;

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
        Debug.Log("改进版Thumbs Up手势识别器已启动");

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
        yield return new WaitForSeconds(2f); // 等待更长时间确保系统完全初始化

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

            // 获取HolisticTrackingSolution组件
            var holisticSolution = solution.GetComponent<Mediapipe.Unity.Sample.Holistic.HolisticTrackingSolution>();
            if (holisticSolution != null)
            {
                Debug.Log("成功连接到HolisticTrackingSolution");

                // 尝试多种方式获取graphRunner
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
            // 方法1：通过反射获取graphRunner
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

            // 方法2：通过属性反射获取graphRunner
            var graphRunnerProperty = holisticSolution.GetType().GetProperty("graphRunner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

            if (graphRunnerProperty != null)
            {
                var graphRunner = graphRunnerProperty.GetValue(holisticSolution) as Mediapipe.Unity.Sample.Holistic.HolisticTrackingGraph;
                if (graphRunner != null)
                {
                    Debug.Log("通过属性反射获取到HolisticTrackingGraph");
                    graphRunner.OnLeftHandLandmarksOutput += OnLeftHandLandmarksReceived;
                    graphRunner.OnRightHandLandmarksOutput += OnRightHandLandmarksReceived;
                    Debug.Log("✓ 成功注册手势回调函数");
                    return;
                }
            }

            Debug.LogWarning("所有反射方法都失败，将使用轮询方式获取数据");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"注册回调时出错: {e.Message}");
            Debug.LogWarning("将使用轮询方式获取数据");
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
        // 模拟thumbs up关键点数据
        for (int i = 0; i < 21; i++)
        {
            currentLandmarks[i] = new Vector3(
                Random.Range(0.4f, 0.6f),  // X
                Random.Range(0.4f, 0.6f),  // Y  
                Random.Range(-0.05f, 0.05f)  // Z
            );
        }

        // 设置明显的thumbs up姿态
        currentLandmarks[0] = new Vector3(0.5f, 0.7f, 0f);   // 手腕
        currentLandmarks[2] = new Vector3(0.5f, 0.6f, 0f);   // 拇指掌关节
        currentLandmarks[4] = new Vector3(0.5f, 0.4f, 0f);   // 拇指尖（明显向上）
        
        // 其他手指都收拢（比手腕更低或者接近手腕）
        currentLandmarks[8] = new Vector3(0.52f, 0.75f, 0f);   // 食指尖
        currentLandmarks[12] = new Vector3(0.48f, 0.75f, 0f);  // 中指尖
        currentLandmarks[16] = new Vector3(0.46f, 0.74f, 0f);  // 无名指尖
        currentLandmarks[20] = new Vector3(0.44f, 0.73f, 0f);  // 小指尖

        hasHandData = true;
        landmarksUpdated = true;
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

    void Update()
    {
        frameCount++;

        // 保留键盘测试功能
        if (Input.GetKey(KeyCode.T))
        {
            thumbsUpDetected = true;
        }

        // 检查手势计时
        if (thumbsUpDetected)
        {
            thumbsUpTimer += Time.deltaTime;

            if (thumbsUpTimer >= gestureHoldTime && !hasTriggered && Time.time - lastGestureTime > gestureResetTime)
            {
                TriggerContentSwitch();
            }
        }
        else
        {
            thumbsUpTimer = 0f;
        }
    }

    void AnalyzeHandGesture()
    {
        // 检查当前面板，只在Welcome面板时检测Thumbs Up
        if (contentSwitcher != null && !contentSwitcher.IsInWelcomeState())
        {
            if (!Input.GetKey(KeyCode.T))
            {
                thumbsUpDetected = false;
                thumbsUpStableCount = 0;
            }
            return;
        }

        if (!landmarksUpdated || !hasHandData)
        {
            // 如果没有手部数据，重置检测状态
            if (!Input.GetKey(KeyCode.T))
            {
                thumbsUpDetected = false;
                thumbsUpStableCount = 0;
            }
            return;
        }

        // 分析当前手部关键点
        bool isThumbsUp = DetectThumbsUpGesture();

        // 稳定性检测
        if (isThumbsUp)
        {
            thumbsUpStableCount++;
            if (thumbsUpStableCount >= requiredStableFrames)
            {
                thumbsUpDetected = true;
                Debug.Log("✓ 检测到稳定的Thumbs Up手势！");
            }
        }
        else if (!Input.GetKey(KeyCode.T))
        {
            thumbsUpStableCount = 0;
            thumbsUpDetected = false;
        }

        landmarksUpdated = false; // 重置更新标志
    }

    bool DetectThumbsUpGesture()
    {
        try
        {
            // 关键点位置 - MediaPipe手部21个关键点
            Vector3 thumbTip = currentLandmarks[4];       // 拇指尖
            Vector3 thumbIp = currentLandmarks[3];        // 拇指第一关节
            Vector3 thumbMcp = currentLandmarks[2];       // 拇指掌关节
            Vector3 thumbCmc = currentLandmarks[1];       // 拇指腕掌关节
            Vector3 wrist = currentLandmarks[0];          // 手腕
            
            // 其他手指指尖
            Vector3 indexTip = currentLandmarks[8];       // 食指尖
            Vector3 middleTip = currentLandmarks[12];     // 中指尖
            Vector3 ringTip = currentLandmarks[16];       // 无名指尖
            Vector3 pinkyTip = currentLandmarks[20];      // 小指尖
            
            // 其他手指的掌关节（用于判断弯曲）
            Vector3 indexMcp = currentLandmarks[5];       // 食指掌关节
            Vector3 middleMcp = currentLandmarks[9];      // 中指掌关节
            Vector3 ringMcp = currentLandmarks[13];       // 无名指掌关节
            Vector3 pinkyMcp = currentLandmarks[17];      // 小指掌关节
            
            // 检查关键点是否有效
            if (thumbTip == Vector3.zero || wrist == Vector3.zero || 
                indexTip == Vector3.zero || middleTip == Vector3.zero)
            {
                if (showDetailedAnalysis)
                    Debug.Log("❌ 关键点数据无效");
                return false;
            }
            
            // === 核心检测1：拇指明显向上伸直 ===
            float thumbHeight = wrist.y - thumbTip.y;  // 拇指相对手腕的高度（Y坐标小的在上方）
            bool thumbPointsUp = thumbHeight > thumbUpThreshold;
            
            // 拇指关节链检测：每个关节都应该比前一个更高（Y坐标更小）
            bool thumbJointsProgressive = (thumbCmc.y > thumbMcp.y) && 
                                         (thumbMcp.y > thumbIp.y) && 
                                         (thumbIp.y > thumbTip.y);
            
            // === 核心检测2：拇指是最突出/最高的手指 ===
            // 拇指应该比其他手指更高（Y坐标更小）
            bool thumbIsHighest = (thumbTip.y < indexTip.y - fingerHeightDifference) &&
                                 (thumbTip.y < middleTip.y - fingerHeightDifference) &&
                                 (thumbTip.y < ringTip.y - fingerHeightDifference) &&
                                 (thumbTip.y < pinkyTip.y - fingerHeightDifference);
            
            // === 核心检测3：其他四指都弯曲/收拢 ===
            // 检测方法：指尖应该比掌关节更靠近手腕，或者指尖比掌关节更低
            float indexBendScore = Vector3.Distance(indexTip, wrist) - Vector3.Distance(indexMcp, wrist);
            float middleBendScore = Vector3.Distance(middleTip, wrist) - Vector3.Distance(middleMcp, wrist);
            float ringBendScore = Vector3.Distance(ringTip, wrist) - Vector3.Distance(ringMcp, wrist);
            float pinkyBendScore = Vector3.Distance(pinkyTip, wrist) - Vector3.Distance(pinkyMcp, wrist);
            
            // 弯曲检测：指尖距离手腕应该小于或接近掌关节距离
            bool indexBent = indexBendScore < fingerBendThreshold;
            bool middleBent = middleBendScore < fingerBendThreshold;
            bool ringBent = ringBendScore < fingerBendThreshold;
            bool pinkyBent = pinkyBendScore < fingerBendThreshold;
            
            // 至少3个手指弯曲（允许一个手指稍微伸出）
            int bentFingerCount = 0;
            if (indexBent) bentFingerCount++;
            if (middleBent) bentFingerCount++;
            if (ringBent) bentFingerCount++;
            if (pinkyBent) bentFingerCount++;
            bool mostFingersBent = bentFingerCount >= 3;
            
            // === 核心检测4：手势方向检测 ===
            // 拇指应该垂直向上，不是斜向
            float thumbAngle = Mathf.Atan2(thumbTip.y - thumbMcp.y, thumbTip.x - thumbMcp.x) * Mathf.Rad2Deg;
            // 调整角度计算，因为Y轴向下为正
            thumbAngle = -thumbAngle + 90f; // 转换为向上为0度
            bool thumbVertical = Mathf.Abs(thumbAngle) < maxThumbAngle; // 允许偏差
            
            if (showDetailedAnalysis)
            {
                Debug.Log($"=== Thumbs Up 详细分析 ===");
                Debug.Log($"1. 拇指向上:");
                Debug.Log($"   拇指高度: {thumbHeight:F3} > {thumbUpThreshold:F3} = {thumbPointsUp}");
                Debug.Log($"   关节递进: {thumbJointsProgressive}");
                Debug.Log($"2. 拇指突出:");
                Debug.Log($"   拇指Y: {thumbTip.y:F3}");
                Debug.Log($"   食指Y: {indexTip.y:F3} (差值: {(indexTip.y - thumbTip.y):F3})");
                Debug.Log($"   拇指最高: {thumbIsHighest}");
                Debug.Log($"3. 其他手指弯曲:");
                Debug.Log($"   食指弯曲分数: {indexBendScore:F3} < {fingerBendThreshold:F3} = {indexBent}");
                Debug.Log($"   中指弯曲分数: {middleBendScore:F3} < {fingerBendThreshold:F3} = {middleBent}");
                Debug.Log($"   无名指弯曲分数: {ringBendScore:F3} < {fingerBendThreshold:F3} = {ringBent}");
                Debug.Log($"   小指弯曲分数: {pinkyBendScore:F3} < {fingerBendThreshold:F3} = {pinkyBent}");
                Debug.Log($"   弯曲手指数: {bentFingerCount}/4, 足够弯曲: {mostFingersBent}");
                Debug.Log($"4. 拇指角度:");
                Debug.Log($"   拇指角度: {thumbAngle:F1}°, 垂直度: {thumbVertical}");
            }
            
            // === 综合判断 ===
            bool result = false;
            string mode = "";
            
            if (useStrictMode)
            {
                // 严格模式：所有条件都满足
                result = thumbPointsUp && thumbJointsProgressive && thumbIsHighest && 
                        mostFingersBent && thumbVertical;
                mode = "严格模式";
            }
            else if (useStandardMode)
            {
                // 标准模式：核心条件满足
                result = thumbPointsUp && thumbIsHighest && mostFingersBent;
                mode = "标准模式";
            }
            else if (allowRelaxedDetection)
            {
                // 宽松模式：基本条件满足
                result = thumbPointsUp && (mostFingersBent || thumbIsHighest);
                mode = "宽松模式";
            }
            
            if (showDetailedAnalysis)
            {
                Debug.Log($"判断结果:");
                Debug.Log($"   严格模式: {(thumbPointsUp && thumbJointsProgressive && thumbIsHighest && mostFingersBent && thumbVertical)}");
                Debug.Log($"   标准模式: {(thumbPointsUp && thumbIsHighest && mostFingersBent)}");
                Debug.Log($"   宽松模式: {(thumbPointsUp && (mostFingersBent || thumbIsHighest))}");
                Debug.Log($"   最终结果: {result} ({mode})");
                Debug.Log($"========================");
            }
            
            return result;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Thumbs Up手势分析出错: {e.Message}");
            return false;
        }
    }

    // 公共方法：供MediaPipe系统调用来更新手部关键点
    public void UpdateHandLandmarks(Vector3[] landmarks)
    {
        if (landmarks != null && landmarks.Length >= 21)
        {
            System.Array.Copy(landmarks, currentLandmarks, 21);
            hasHandData = true;
            landmarksUpdated = true;
        }
        else
        {
            hasHandData = false;
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
            
            Debug.Log($"✓ ThumbUp接收到真实MediaPipe数据：{landmarks.Count}个关键点");
            Debug.Log($"拇指尖真实位置: {currentLandmarks[4]}");
            Debug.Log($"手腕真实位置: {currentLandmarks[0]}");
        }
        else
        {
            hasHandData = false;
            Debug.Log("✗ ThumbUp手部数据不足，设置hasHandData=false");
        }
    }

    // 供外部调用的简化接口
    public void OnHandDetected(bool detected)
    {
        hasHandData = detected;
        if (!detected)
        {
            thumbsUpDetected = false;
            thumbsUpStableCount = 0;
        }
    }

    void TriggerContentSwitch()
    {
        hasTriggered = true;
        lastGestureTime = Time.time;
        Debug.Log("Thumbs Up手势确认，切换到目标界面！");

        if (contentSwitcher != null)
        {
            contentSwitcher.SwitchToTarget();
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
        thumbsUpDetected = false;
        thumbsUpStableCount = 0;
        Debug.Log("重置Thumbs Up手势检测，可以再次触发");
    }

    void OnGUI()
    {
        if (showDebugInfo)
        {
            GUILayout.BeginArea(new UnityRect(10, 100, 400, 400)); // 增加高度
            GUILayout.Label("=== 改进版Thumbs Up手势识别 ===");
            
            // 显示当前面板状态
            if (contentSwitcher != null)
            {
                bool isWelcomeState = contentSwitcher.IsInWelcomeState();
                GUILayout.Label($"当前面板: {(isWelcomeState ? "Welcome" : "其他")}");
                GUILayout.Label($"检测启用: {(isWelcomeState ? "是" : "否")}");
            }
            else
            {
                GUILayout.Label("ContentSwitcher: 未连接");
            }
            
            GUILayout.Label($"Thumbs Up: {(thumbsUpDetected ? "检测中" : "未检测")}");
            GUILayout.Label($"稳定计数: {thumbsUpStableCount}/{requiredStableFrames}");
            GUILayout.Label($"计时器: {thumbsUpTimer:F1}s / {gestureHoldTime}s");
            GUILayout.Label($"已触发: {(hasTriggered ? "是" : "否")}");
            GUILayout.Label($"手部数据: {(hasHandData ? "有数据" : "无数据")}");
            GUILayout.Label($"数据更新: {(landmarksUpdated ? "是" : "否")}");
            
            GUILayout.Space(5);
            GUILayout.Label("检测模式:");
            GUILayout.Label($"  严格模式: {(useStrictMode ? "启用" : "禁用")}");
            GUILayout.Label($"  标准模式: {(useStandardMode ? "启用" : "禁用")}");
            GUILayout.Label($"  宽松模式: {(allowRelaxedDetection ? "启用" : "禁用")}");
            
            GUILayout.Label("按住T键测试 或 做真实Thumbs Up手势");
            GUILayout.Label("(仅在Welcome面板时检测)");

            if (hasHandData && showLandmarkInfo)
            {
                GUILayout.Label("--- 实时手部关键点 ---");
                GUILayout.Label($"拇指尖: {currentLandmarks[4]:F3}");
                GUILayout.Label($"手腕: {currentLandmarks[0]:F3}");
                GUILayout.Label($"食指尖: {currentLandmarks[8]:F3}");
                
                // 显示简化的计算结果
                if (currentLandmarks[4] != Vector3.zero)
                {
                    float thumbHeight = currentLandmarks[0].y - currentLandmarks[4].y;
                    bool thumbUp = thumbHeight > thumbUpThreshold;
                    GUILayout.Label($"拇指向上: {thumbUp} (高度: {thumbHeight:F3})");
                }
            }

            GUILayout.EndArea();
        }
    }

    // 在Scene视图中显示手部关键点
    void OnDrawGizmos()
    {
        if (!hasHandData || !showLandmarkInfo) return;

        Gizmos.color = UnityEngine.Color.green;

        // 绘制拇指尖
        if (currentLandmarks[4] != Vector3.zero)
        {
            Vector3 worldPos = Camera.main.ScreenToWorldPoint(new Vector3(
                currentLandmarks[4].x * UnityEngine.Screen.width,
                (1 - currentLandmarks[4].y) * UnityEngine.Screen.height,
                5f
            ));
            Gizmos.DrawSphere(worldPos, 0.1f);
        }
    }
}