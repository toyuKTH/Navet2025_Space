using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;

public class SimpleHandPalmGestureController : MonoBehaviour
{
    [Header("缩放设置")]
    public float zoomSpeed = 2f;
    public float minZoom = 10f;
    public float maxZoom = 80f;
    
    [Header("左右移动设置")]
    public float panSpeed = 5f;
    public float panLimit = 20f; // 左右移动的最大距离
    
    [Header("移动平滑")]
    public float smoothTime = 0.3f;
    
    [Header("手势检测设置")]
    public float zoomSensitivity = 10f; // 缩放敏感度
    public float panSensitivity = 15f; // 平移敏感度
    public float minHandDistance = 0.1f; // 最小手掌距离
    public float maxHandDistance = 0.8f; // 最大手掌距离
    public float distanceChangeThreshold = 0.01f; // 距离变化阈值（判断缩放意图）
    public float panChangeThreshold = 0.008f; // 平移变化阈值（判断平移意图）
    
    [Header("调试信息")]
    public bool showDebugInfo = true;
    public bool showDetailedAnalysis = false;
    
    private Camera mainCamera;
    private Vector3 targetPosition;
    private float targetZoom;
    private Vector3 positionVelocity;
    private float zoomVelocity;
    
    // 手部数据 - 简化版，参考ThumbUp脚本
    private Vector3[] leftHandLandmarks = new Vector3[21];
    private Vector3[] rightHandLandmarks = new Vector3[21];
    private bool hasLeftHandData = false;
    private bool hasRightHandData = false;
    private bool landmarksUpdated = false;
    
    // 手势检测相关 - 简化逻辑
    private Vector3 leftPalmCenter = Vector3.zero;
    private Vector3 rightPalmCenter = Vector3.zero;
    private float previousHandDistance = 0f;
    private Vector3 previousHandsCenterPosition = Vector3.zero;
    private Vector3 previousSingleHandPosition = Vector3.zero;
    private bool isFirstFrame = true;
    private int frameCount = 0;
    
    // 手势模式判断 - 简化
    private enum GestureMode { None, Zoom, Pan }
    private GestureMode currentMode = GestureMode.None;
    
    void Start()
    {
        mainCamera = GetComponent<Camera>();
        if (mainCamera == null)
            mainCamera = Camera.main;
            
        // 初始化目标值
        targetPosition = transform.position;
        targetZoom = mainCamera.fieldOfView;
        
        Debug.Log("简化版手掌手势控制已启动 - 双手缩放，单手/双手平移");
        
        // 连接到MediaPipe系统 - 复用ThumbUp的连接方式
        StartCoroutine(ConnectToHandDetection());
    }
    
    IEnumerator ConnectToHandDetection()
    {
        yield return new WaitForSeconds(2f); // 等待系统初始化
        
        // 尝试连接到Holistic系统
        TryConnectToHolisticSystem();
        
        // 启动手势分析循环 - 和ThumbUp一样的频率
        InvokeRepeating("AnalyzeHandGestures", 0.1f, 0.1f);
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
            // 完全复用ThumbUp的反射方式
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
            
            // 尝试属性反射
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
            
            Debug.LogWarning("反射获取graphRunner失败");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"注册回调时出错: {e.Message}");
        }
    }
    
    // 左手数据回调 - 完全复用ThumbUp的方式
    void OnLeftHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs eventArgs)
    {
        var packet = eventArgs.packet;
        if (packet != null)
        {
            var landmarks = packet.Get(NormalizedLandmarkList.Parser);
            if (landmarks != null && landmarks.Landmark.Count >= 21)
            {
                UpdateLeftHandLandmarks(landmarks.Landmark);
                if (showDetailedAnalysis)
                    Debug.Log($"👈 成功处理左手数据: {landmarks.Landmark.Count}个点");
            }
        }
    }
    
    // 右手数据回调
    void OnRightHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs eventArgs)
    {
        var packet = eventArgs.packet;
        if (packet != null)
        {
            var landmarks = packet.Get(NormalizedLandmarkList.Parser);
            if (landmarks != null && landmarks.Landmark.Count >= 21)
            {
                UpdateRightHandLandmarks(landmarks.Landmark);
                if (showDetailedAnalysis)
                    Debug.Log($"👉 成功处理右手数据: {landmarks.Landmark.Count}个点");
            }
        }
    }
    
    void Update()
    {
        frameCount++;
        ApplySmoothMovement();
        
        // 键盘测试（仅用于调试）
        if (Input.GetKey(KeyCode.Q))
        {
            targetZoom -= zoomSpeed * Time.deltaTime * 10f;
            targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
        }
        if (Input.GetKey(KeyCode.E))
        {
            targetZoom += zoomSpeed * Time.deltaTime * 10f;
            targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
        }
    }
    
    void AnalyzeHandGestures()
    {
        if (!landmarksUpdated)
            return;
        
        // 计算手掌中心位置 - 简化版
        CalculatePalmCenters();
        
        // 简化的手势检测逻辑
        DetectAndExecuteGesture();
        
        landmarksUpdated = false;
    }
    
    void CalculatePalmCenters()
    {
        if (hasLeftHandData && leftHandLandmarks.Length >= 18)
        {
            // 简单使用手腕作为手掌中心，最可靠
            leftPalmCenter = leftHandLandmarks[0]; // 手腕位置
        }
        
        if (hasRightHandData && rightHandLandmarks.Length >= 18)
        {
            rightPalmCenter = rightHandLandmarks[0]; // 手腕位置
        }
    }
    
    void DetectAndExecuteGesture()
    {
        if (isFirstFrame)
        {
            InitializePreviousPositions();
            isFirstFrame = false;
            return;
        }
        
        bool zoomGestureDetected = false;
        bool panGestureDetected = false;
        
        // 检测缩放手势：需要双手且距离有明显变化
        if (hasLeftHandData && hasRightHandData)
        {
            float currentDistance = Vector3.Distance(leftPalmCenter, rightPalmCenter);
            float distanceChange = Mathf.Abs(currentDistance - previousHandDistance);
            
            if (distanceChange > distanceChangeThreshold)
            {
                zoomGestureDetected = true;
                ExecuteZoomGesture(currentDistance);
            }
        }
        
        // 检测平移手势：单手或双手有明显左右移动
        Vector3 currentHandPosition = Vector3.zero;
        Vector3 previousHandPosition = Vector3.zero;
        bool hasValidHandForPan = false;
        
        if (hasLeftHandData && hasRightHandData)
        {
            // 双手情况：使用双手中心位置判断平移
            currentHandPosition = (leftPalmCenter + rightPalmCenter) / 2f;
            previousHandPosition = previousHandsCenterPosition;
            hasValidHandForPan = true;
        }
        else if (hasLeftHandData || hasRightHandData)
        {
            // 单手情况
            currentHandPosition = hasLeftHandData ? leftPalmCenter : rightPalmCenter;
            previousHandPosition = previousSingleHandPosition;
            hasValidHandForPan = true;
        }
        
        if (hasValidHandForPan)
        {
            float horizontalChange = Mathf.Abs(currentHandPosition.x - previousHandPosition.x);
            if (horizontalChange > panChangeThreshold)
            {
                panGestureDetected = true;
                ExecutePanGesture(currentHandPosition, previousHandPosition);
            }
        }
        
        // 更新模式显示
        if (zoomGestureDetected && panGestureDetected)
        {
            // 同时检测到，优先缩放
            currentMode = GestureMode.Zoom;
        }
        else if (zoomGestureDetected)
        {
            currentMode = GestureMode.Zoom;
        }
        else if (panGestureDetected)
        {
            currentMode = GestureMode.Pan;
        }
        else
        {
            currentMode = GestureMode.None;
        }
        
        // 更新历史位置
        UpdatePreviousPositions();
    }
    
    void ExecuteZoomGesture(float currentDistance)
    {
        if (!hasLeftHandData || !hasRightHandData)
            return;
        
        currentDistance = Mathf.Clamp(currentDistance, minHandDistance, maxHandDistance);
        
        // 计算距离变化
        float distanceDelta = currentDistance - previousHandDistance;
        
        // 双手距离增大 = 放大视野 = 减小FOV
        // 双手距离缩小 = 缩小视野 = 增大FOV
        float zoomChange = -distanceDelta * zoomSpeed * zoomSensitivity;
        targetZoom += zoomChange;
        targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
        
        if (showDetailedAnalysis)
        {
            Debug.Log($"🔍 缩放手势 - 距离: {currentDistance:F3}, 变化: {distanceDelta:F3}, " +
                     $"缩放变化: {zoomChange:F3}, FOV: {targetZoom:F1}");
        }
    }
    
    void ExecutePanGesture(Vector3 currentPos, Vector3 previousPos)
    {
        // 计算手掌在X轴上的移动
        float deltaX = currentPos.x - previousPos.x;
        
        // 向右挥动 = 相机右移
        // 向左挥动 = 相机左移
        float movement = deltaX * panSpeed * panSensitivity;
        targetPosition.x += movement;
        
        // 限制移动范围
        targetPosition.x = Mathf.Clamp(targetPosition.x, -panLimit, panLimit);
        
        if (showDetailedAnalysis)
        {
            string handType = "";
            if (hasLeftHandData && hasRightHandData)
                handType = "双手";
            else if (hasLeftHandData)
                handType = "左手";
            else
                handType = "右手";
                
            Debug.Log($"👋 平移手势({handType}) - 移动: {deltaX:F3}, 位移: {movement:F3}, " +
                     $"目标X: {targetPosition.x:F2}");
        }
    }
    
    void InitializePreviousPositions()
    {
        if (hasLeftHandData && hasRightHandData)
        {
            previousHandDistance = Vector3.Distance(leftPalmCenter, rightPalmCenter);
            previousHandsCenterPosition = (leftPalmCenter + rightPalmCenter) / 2f;
        }
        
        if (hasLeftHandData || hasRightHandData)
        {
            previousSingleHandPosition = hasLeftHandData ? leftPalmCenter : rightPalmCenter;
        }
    }
    
    void UpdatePreviousPositions()
    {
        if (hasLeftHandData && hasRightHandData)
        {
            previousHandDistance = Vector3.Distance(leftPalmCenter, rightPalmCenter);
            previousHandsCenterPosition = (leftPalmCenter + rightPalmCenter) / 2f;
        }
        
        if (hasLeftHandData || hasRightHandData)
        {
            previousSingleHandPosition = hasLeftHandData ? leftPalmCenter : rightPalmCenter;
        }
    }
    
    void ApplySmoothMovement()
    {
        // 平滑移动相机
        transform.position = Vector3.SmoothDamp(transform.position, targetPosition, ref positionVelocity, smoothTime);
        
        // 平滑缩放
        mainCamera.fieldOfView = Mathf.SmoothDamp(mainCamera.fieldOfView, targetZoom, ref zoomVelocity, smoothTime);
    }
    
    // 更新左手关键点 - 完全复用ThumbUp的方式
    public void UpdateLeftHandLandmarks(Google.Protobuf.Collections.RepeatedField<NormalizedLandmark> landmarks)
    {
        if (landmarks != null && landmarks.Count >= 21)
        {
            try
            {
                for (int i = 0; i < 21 && i < landmarks.Count; i++)
                {
                    var landmark = landmarks[i];
                    leftHandLandmarks[i] = new Vector3(landmark.X, landmark.Y, landmark.Z);
                }
                hasLeftHandData = true;
                landmarksUpdated = true;
                
                if (showDetailedAnalysis)
                {
                    Debug.Log($"✓ 左手数据更新：{landmarks.Count}个关键点");
                    Debug.Log($"左手腕位置: {leftHandLandmarks[0]}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"更新左手关键点时出错: {e.Message}");
                hasLeftHandData = false;
            }
        }
        else
        {
            hasLeftHandData = false;
            if (showDetailedAnalysis)
                Debug.Log("✗ 左手数据不足，设置hasLeftHandData=false");
        }
    }
    
    // 更新右手关键点
    public void UpdateRightHandLandmarks(Google.Protobuf.Collections.RepeatedField<NormalizedLandmark> landmarks)
    {
        if (landmarks != null && landmarks.Count >= 21)
        {
            try
            {
                for (int i = 0; i < 21 && i < landmarks.Count; i++)
                {
                    var landmark = landmarks[i];
                    rightHandLandmarks[i] = new Vector3(landmark.X, landmark.Y, landmark.Z);
                }
                hasRightHandData = true;
                landmarksUpdated = true;
                
                if (showDetailedAnalysis)
                {
                    Debug.Log($"✓ 右手数据更新：{landmarks.Count}个关键点");
                    Debug.Log($"右手腕位置: {rightHandLandmarks[0]}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"更新右手关键点时出错: {e.Message}");
                hasRightHandData = false;
            }
        }
        else
        {
            hasRightHandData = false;
            if (showDetailedAnalysis)
                Debug.Log("✗ 右手数据不足，设置hasRightHandData=false");
        }
    }
    
    void OnGUI()
    {
        if (showDebugInfo)
        {
            GUILayout.BeginArea(new UnityEngine.Rect(10, 10, 400, 300));
            GUILayout.Label("=== 简化版手掌手势控制 ===");
            
            GUILayout.Label($"左手检测: {(hasLeftHandData ? "✓" : "✗")}");
            GUILayout.Label($"右手检测: {(hasRightHandData ? "✓" : "✗")}");
            
            if (hasLeftHandData)
                GUILayout.Label($"左手腕: {leftPalmCenter}");
            if (hasRightHandData)
                GUILayout.Label($"右手腕: {rightPalmCenter}");
            
            if (hasLeftHandData && hasRightHandData)
            {
                float distance = Vector3.Distance(leftPalmCenter, rightPalmCenter);
                GUILayout.Label($"双手距离: {distance:F3}");
            }
            
            GUILayout.Label($"当前手势模式: {currentMode}");
            GUILayout.Label($"当前FOV: {mainCamera.fieldOfView:F1}");
            GUILayout.Label($"相机X位置: {transform.position.x:F2}");
            GUILayout.Label($"帧数: {frameCount}");
            
            GUILayout.Space(5);
            GUILayout.Label("测试按键: Q缩小 E放大");
            
            GUILayout.EndArea();
        }
    }
}