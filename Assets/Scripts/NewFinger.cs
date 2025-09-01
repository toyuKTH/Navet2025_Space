using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using System.Collections;

public class HandGestureController : MonoBehaviour
{
    [System.Serializable]
    public class HandGestureData
    {
        [Header("手势基础信息")]
        public int fingerAmount = 1;
        public string displayName = "One Finger";
        
        [Header("切换目标")]
        public GameObject destinationPanel;
        
        [Header("相机动画")]
        public string animationTriggerKey = "Gesture1Finger"; // 你制作的动画触发器名称
        public float switchDelay = 1.0f; // 动画播放完成后的延迟时间
        
        [Header("声音效果（可选）")]
        public AudioClip soundEffect;
        public float audioVolume = 1f;
    }

    [Header("核心组件引用")]
    public HolisticTrackingSolution mediaTracker;
    public SimplePanelSwitcher panelController; // 改为SimplePanelSwitcher
    public GameObject mainPlanetPanel;
    
    [Header("动画系统")]
    public Animator cameraAnimator; // 主相机动画控制器
    public AudioSource soundPlayer; // 音效播放器
    
    [Header("1指手势配置")]
    public GameObject oneFingerTargetPanel; // 1指目标面板
    public string oneFingerTriggerName = "toPlanet1"; // 1指动画触发器
    public AudioClip oneFingerSound; // 1指音效
    public float oneFingerDelay = 1.0f; // 1指延迟时间
    public GameObject[] oneFingerHideObjects; // 1指时要隐藏的物体
    public GameObject[] oneFingerShowObjects; // 1指时要显示的物体
    
    [Header("2指手势配置")]
    public GameObject twoFingerTargetPanel; // 2指目标面板
    public string twoFingerTriggerName = "toPlanet2"; // 2指动画触发器
    public AudioClip twoFingerSound; // 2指音效
    public float twoFingerDelay = 1.0f; // 2指延迟时间
    public GameObject[] twoFingerHideObjects; // 2指时要隐藏的物体
    public GameObject[] twoFingerShowObjects; // 2指时要显示的物体
    
    [Header("3指手势配置")]
    public GameObject threeFingerTargetPanel; // 3指目标面板
    public string threeFingerTriggerName = "toPlanet3"; // 3指动画触发器
    public AudioClip threeFingerSound; // 3指音效
    public float threeFingerDelay = 1.0f; // 3指延迟时间
    public GameObject[] threeFingerHideObjects; // 3指时要隐藏的物体
    public GameObject[] threeFingerShowObjects; // 3指时要显示的物体
    
    [Header("检测参数")]
    public float gestureHoldDuration = 0.5f;
    public int minStableFrameCount = 2;
    public float fingerDetectionThreshold = 0.05f;

    private HolisticTrackingGraph trackingGraph;
    private Vector3[] landmarkPositions = new Vector3[21];
    private bool hasNewFrame;
    private int stableFrameCounter;
    private float gestureTimer;
    private int detectedFingerCount;
    private bool isPlayingAnimation; // 防止动画期间重复触发
    
    void Start()
    {
        StartCoroutine(InitializeSystem());
        
        // 初始化音频组件
        if (soundPlayer == null)
            soundPlayer = GetComponent<AudioSource>();
    }

    IEnumerator InitializeSystem()
    {
        yield return new WaitForSeconds(1.0f);
        
        if (mediaTracker == null)
        {
            var solutionObject = GameObject.Find("Solution");
            mediaTracker = solutionObject ? solutionObject.GetComponent<HolisticTrackingSolution>() : null;
        }
        
        if (mediaTracker == null)
        {
            Debug.LogError("[HandGesture] ❌ 找不到 Holistic 组件");
            yield break;
        }
        
        var graphField = typeof(HolisticTrackingSolution).GetField("graphRunner", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        trackingGraph = graphField?.GetValue(mediaTracker) as HolisticTrackingGraph;
        
        if (trackingGraph == null)
        {
            Debug.LogError("[HandGesture] ❌ 找不到 Graph 组件");
            yield break;
        }
        
        trackingGraph.OnRightHandLandmarksOutput += ProcessHandData;
        trackingGraph.OnLeftHandLandmarksOutput += ProcessHandData;
        InvokeRepeating(nameof(AnalyzeGesture), 0.1f, 0.1f);
        Debug.Log("[HandGesture] ✅ 手势系统初始化完成");
    }

    void OnDestroy()
    {
        if (trackingGraph != null)
        {
            trackingGraph.OnRightHandLandmarksOutput -= ProcessHandData;
            trackingGraph.OnLeftHandLandmarksOutput -= ProcessHandData;
        }
        CancelInvoke(nameof(AnalyzeGesture));
    }

    void ProcessHandData(object sender, OutputStream<NormalizedLandmarkList>.OutputEventArgs eventArgs)
    {
        var landmarkList = eventArgs.packet?.Get(NormalizedLandmarkList.Parser);
        if (landmarkList == null || landmarkList.Landmark.Count < 21) return;
        
        for (int i = 0; i < 21; i++)
        {
            var point = landmarkList.Landmark[i];
            landmarkPositions[i] = new Vector3(point.X, point.Y, point.Z);
        }
        hasNewFrame = true;
    }

    void AnalyzeGesture()
    {
        if (panelController == null || panelController.current != mainPlanetPanel || isPlayingAnimation) return;
        if (!hasNewFrame) return;

        int fingerCount = CalculateFingerCount(landmarkPositions);
        
        if (fingerCount > 0)
        {
            if (fingerCount == detectedFingerCount)
            {
                stableFrameCounter++;
                gestureTimer += 0.1f;
                
                if (stableFrameCounter >= minStableFrameCount && gestureTimer >= gestureHoldDuration)
                {
                    Debug.Log($"[HandGesture] ✅ 检测到稳定手势: {fingerCount} 根手指");
                    
                    // 直接根据手指数执行对应动画
                    StartCoroutine(ExecuteGestureByFingerCount(fingerCount));
                    
                    // 重置检测状态
                    ResetDetectionState();
                }
            }
            else
            {
                detectedFingerCount = fingerCount;
                stableFrameCounter = 1;
                gestureTimer = 0.1f;
            }
        }
        else
        {
            ResetDetectionState();
        }
        
        hasNewFrame = false;
    }

    void ResetDetectionState()
    {
        stableFrameCounter = 0;
        gestureTimer = 0f;
        detectedFingerCount = 0;
    }

    /// <summary>
    /// 根据手指数量执行对应的动画
    /// </summary>
    IEnumerator ExecuteGestureByFingerCount(int fingerCount)
    {
        isPlayingAnimation = true;
        
        GameObject targetPanel = null;
        string triggerName = "";
        AudioClip soundEffect = null;
        float delay = 1.0f;
        GameObject[] hideObjects = null;
        GameObject[] showObjects = null;
        
        // 根据手指数量选择对应的配置
        switch (fingerCount)
        {
            case 1:
                targetPanel = oneFingerTargetPanel;
                triggerName = oneFingerTriggerName;
                soundEffect = oneFingerSound;
                delay = oneFingerDelay;
                hideObjects = oneFingerHideObjects;
                showObjects = oneFingerShowObjects;
                break;
            case 2:
                targetPanel = twoFingerTargetPanel;
                triggerName = twoFingerTriggerName;
                soundEffect = twoFingerSound;
                delay = twoFingerDelay;
                hideObjects = twoFingerHideObjects;
                showObjects = twoFingerShowObjects;
                break;
            case 3:
                targetPanel = threeFingerTargetPanel;
                triggerName = threeFingerTriggerName;
                soundEffect = threeFingerSound;
                delay = threeFingerDelay;
                hideObjects = threeFingerHideObjects;
                showObjects = threeFingerShowObjects;
                break;
            default:
                Debug.LogWarning($"[HandGesture] ⚠️ 未配置 {fingerCount} 指手势");
                isPlayingAnimation = false;
                yield break;
        }
        
        Debug.Log($"[HandGesture] 🎬 开始执行 {fingerCount} 指手势动画");
        
        // 1. 播放音效（可选）
        if (soundPlayer != null && soundEffect != null)
        {
            soundPlayer.PlayOneShot(soundEffect);
        }
        
        // 2. 触发相机动画
        if (cameraAnimator != null && !string.IsNullOrEmpty(triggerName))
        {
            cameraAnimator.SetTrigger(triggerName);
            cameraAnimator.SetInteger("FingerCount", fingerCount);
            Debug.Log($"[HandGesture] 🎥 触发相机动画: {triggerName}");
        }
        else
        {
            Debug.LogWarning($"[HandGesture] ⚠️ 相机动画配置缺失");
        }
        
        // 3. 控制物体显示/隐藏
        ControlObjectsVisibility(hideObjects, false); // 隐藏指定物体
        ControlObjectsVisibility(showObjects, true);  // 显示指定物体
        
        // 4. 等待动画播放完成 + 延迟
        yield return new WaitForSeconds(delay);
        
        // 5. 使用SimplePanelSwitcher执行面板切换
        if (targetPanel != null && panelController != null)
        {
            panelController.SwitchTo(targetPanel);
            Debug.Log($"[HandGesture] 🎯 切换到面板: {targetPanel.name}");
        }
        else if (targetPanel == null)
        {
            Debug.LogWarning($"[HandGesture] ⚠️ {fingerCount} 指手势未设置目标面板");
        }
        else if (panelController == null)
        {
            Debug.LogWarning($"[HandGesture] ⚠️ 未设置SimplePanelSwitcher");
        }
        
        isPlayingAnimation = false;
    }
    
    /// <summary>
    /// 控制物体的显示/隐藏
    /// </summary>
    void ControlObjectsVisibility(GameObject[] objects, bool visible)
    {
        if (objects == null) return;
        
        foreach (var obj in objects)
        {
            if (obj != null)
            {
                obj.SetActive(visible);
                Debug.Log($"[HandGesture] {(visible ? "显示" : "隐藏")}物体: {obj.name}");
            }
        }
    }

    int CalculateFingerCount(Vector3[] landmarks)
    {
        try
        {
            int upFingerCount = 0;
            // 检测四个手指：tip 比 mcp 高（Y 更小）才算竖直
            if (landmarks[5].y - landmarks[8].y > fingerDetectionThreshold) upFingerCount++;  // 食指
            if (landmarks[9].y - landmarks[12].y > fingerDetectionThreshold) upFingerCount++; // 中指
            if (landmarks[13].y - landmarks[16].y > fingerDetectionThreshold) upFingerCount++; // 无名指
            if (landmarks[17].y - landmarks[20].y > fingerDetectionThreshold) upFingerCount++; // 小指
            
            Debug.Log($"[HandGesture] 食指={landmarks[5].y - landmarks[8].y:F2}, 中指={landmarks[9].y - landmarks[12].y:F2}, " +
                     $"无名指={landmarks[13].y - landmarks[16].y:F2}, 小指={landmarks[17].y - landmarks[20].y:F2} -> 总计={upFingerCount}");
            return upFingerCount;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 手动触发手势动画（用于测试）
    /// </summary>
    [ContextMenu("测试1指手势")]
    void TestOneFingerGesture() 
    {
        StartCoroutine(ExecuteGestureByFingerCount(1));
    }
    
    [ContextMenu("测试2指手势")]
    void TestTwoFingerGesture() 
    {
        StartCoroutine(ExecuteGestureByFingerCount(2));
    }
    
    [ContextMenu("测试3指手势")]
    void TestThreeFingerGesture() 
    {
        StartCoroutine(ExecuteGestureByFingerCount(3));
    }
}