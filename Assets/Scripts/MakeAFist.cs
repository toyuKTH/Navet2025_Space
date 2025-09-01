using UnityEngine;
using System.Collections;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;

public class FistGestureController : MonoBehaviour
{
    [Header("核心组件引用")]
    public HolisticTrackingSolution mediaTracker;
    public SimplePanelSwitcher panelController;
    public GameObject mainPlanetPanel; // 返回的目标面板（主星球面板）

    [Header("握拳手势触发面板")]
    public GameObject[] activePanels;   // 在这些面板上启用握拳返回，例如 Planet1Panel, Planet2Panel, Planet3Panel

    [Header("返回动画设置")]
    public Animator cameraAnimator; // 返回动画控制器
    public string backTriggerName = "BackToMain"; // 返回动画触发器名称
    public float backAnimationDelay = 1.5f; // 返回动画延迟时间
    
    [Header("音效设置")]
    public AudioSource soundPlayer;
    public AudioClip fistGestureSound; // 握拳手势音效
    
    [Header("检测参数")]
    public float gestureHoldDuration = 1.0f;   // 握拳保持多久触发
    public int requiredStableFrames = 3;   // 稳定帧数
    public float cooldownSeconds = 3f;     // 触发后冷却时间

    private HolisticTrackingGraph trackingGraph;
    private Vector3[] landmarkPositions = new Vector3[21];
    private bool hasNewFrame;
    private bool dataUpdated;
    private int stableFrameCounter;
    private float gestureTimer;
    private bool isGestureLocked; // 冷却期间锁定
    private bool isPlayingAnimation; // 动画播放中

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
            Debug.LogError("[FistGesture] ❌ 找不到 Holistic 组件"); 
            yield break; 
        }

        var graphField = typeof(HolisticTrackingSolution).GetField("graphRunner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        trackingGraph = graphField?.GetValue(mediaTracker) as HolisticTrackingGraph;
        
        if (trackingGraph == null) 
        { 
            Debug.LogError("[FistGesture] ❌ 找不到 Graph 组件"); 
            yield break; 
        }

        trackingGraph.OnRightHandLandmarksOutput += ProcessHandData;
        trackingGraph.OnLeftHandLandmarksOutput += ProcessHandData;
        InvokeRepeating(nameof(AnalyzeFistGesture), 0.1f, 0.1f);
        
        Debug.Log("[FistGesture] ✅ 握拳返回系统初始化完成");
    }

    void OnDestroy()
    {
        if (trackingGraph != null)
        {
            trackingGraph.OnRightHandLandmarksOutput -= ProcessHandData;
            trackingGraph.OnLeftHandLandmarksOutput -= ProcessHandData;
        }
        CancelInvoke(nameof(AnalyzeFistGesture));
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
        dataUpdated = true;
    }

    /// <summary>
    /// 检查当前面板是否在激活列表中
    /// </summary>
    bool IsInActivePanels()
    {
        if (panelController == null || panelController.current == null || activePanels == null) 
            return false;
            
        foreach (var panel in activePanels) 
        {
            if (panel == panelController.current) 
                return true;
        }
        return false;
    }

    void AnalyzeFistGesture()
    {
        if (isGestureLocked || !dataUpdated || !hasNewFrame || isPlayingAnimation) return;
        if (!IsInActivePanels()) return;

        if (DetectFist())
        {
            stableFrameCounter++;
            if (stableFrameCounter >= requiredStableFrames)
            {
                gestureTimer += 0.1f;
                if (gestureTimer >= gestureHoldDuration)
                {
                    Debug.Log("[FistGesture] ✊ 检测到稳定握拳手势");
                    StartCoroutine(ExecuteFistGestureReturn());
                }
            }
        }
        else
        {
            ResetGestureState();
        }
        dataUpdated = false;
    }

    void ResetGestureState()
    {
        stableFrameCounter = 0;
        gestureTimer = 0f;
    }

    /// <summary>
    /// 检测握拳手势
    /// </summary>
    bool DetectFist()
    {
        try
        {
            // 四个手指都弯曲（tip 在 MCP 下方，Y值更大）
            bool indexBent = (landmarkPositions[8].y > landmarkPositions[5].y);   // 食指
            bool middleBent = (landmarkPositions[12].y > landmarkPositions[9].y); // 中指
            bool ringBent = (landmarkPositions[16].y > landmarkPositions[13].y);  // 无名指
            bool pinkyBent = (landmarkPositions[20].y > landmarkPositions[17].y); // 小指

            bool isFist = indexBent && middleBent && ringBent && pinkyBent;
            
            if (isFist)
            {
                Debug.Log($"[FistGesture] 握拳检测 - 食指:{indexBent}, 中指:{middleBent}, 无名指:{ringBent}, 小指:{pinkyBent}");
            }
            
            return isFist;
        }
        catch 
        { 
            return false; 
        }
    }

    /// <summary>
    /// 执行握拳返回动画和面板切换
    /// </summary>
    IEnumerator ExecuteFistGestureReturn()
    {
        isPlayingAnimation = true;
        isGestureLocked = true;
        ResetGestureState();

        Debug.Log("[FistGesture] 🎬 开始执行握拳返回动画");

        // 1. 播放音效
        if (soundPlayer != null && fistGestureSound != null)
        {
            soundPlayer.PlayOneShot(fistGestureSound);
        }

        // 2. 触发返回动画
        if (cameraAnimator != null && !string.IsNullOrEmpty(backTriggerName))
        {
            cameraAnimator.SetTrigger(backTriggerName);
            Debug.Log($"[FistGesture] 🎥 触发返回动画: {backTriggerName}");
        }

        // 3. 等待动画播放完成
        yield return new WaitForSeconds(backAnimationDelay);

        // 4. 切换回主面板
        if (mainPlanetPanel != null && panelController != null)
        {
            panelController.SwitchTo(mainPlanetPanel);
            Debug.Log($"[FistGesture] 🎯 返回主面板: {mainPlanetPanel.name}");
        }
        else
        {
            Debug.LogWarning("[FistGesture] ⚠️ 主面板或面板控制器未设置");
        }

        isPlayingAnimation = false;

        // 5. 开始冷却
        StartCoroutine(StartCooldown());
    }

    /// <summary>
    /// 冷却期间禁用手势识别
    /// </summary>
    IEnumerator StartCooldown()
    {
        Debug.Log($"[FistGesture] 🕒 开始冷却 {cooldownSeconds} 秒");
        yield return new WaitForSeconds(cooldownSeconds);
        isGestureLocked = false;
        Debug.Log("[FistGesture] 🔄 冷却结束，可再次识别握拳手势");
    }

    /// <summary>
    /// 手动测试握拳返回（调试用）
    /// </summary>
    [ContextMenu("测试握拳返回")]
    void TestFistReturn()
    {
        if (isGestureLocked || isPlayingAnimation)
        {
            Debug.Log("[FistGesture] 当前处于冷却或动画播放中，无法测试");
            return;
        }
        
        StartCoroutine(ExecuteFistGestureReturn());
    }

    /// <summary>
    /// 重置冷却状态（调试用）
    /// </summary>
    [ContextMenu("重置冷却状态")]
    void ResetCooldown()
    {
        isGestureLocked = false;
        isPlayingAnimation = false;
        ResetGestureState();
        Debug.Log("[FistGesture] 🔄 冷却状态已重置");
    }
}