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
    public GameObject mainPlanetPanel; // 主面板（Welcome/Main）

    [Header("动画（回主时使用；进入子面板由路由配置）")]
    public Animator cameraAnimator;
    public string backTriggerName = "BackToMain";
    public float backAnimationDelay = 1.5f;

    [Header("音效")]
    public AudioSource soundPlayer;
    public AudioClip fistGestureSound;

    [Header("手势检测参数")]
    public float gestureHoldDuration = 1.0f;  // 握拳保持多久触发
    public int requiredStableFrames = 3;      // 稳定帧数
    public float cooldownSeconds = 3f;        // 触发后冷却时间

    // ====== 路由：从 Main 随机进入子面板时使用 ======
    [System.Serializable]
    public class PanelRoute
    {
        public GameObject panel;          // 目标子面板
        public Animator animatorOverride; // 可选：专用 Animator；为空则使用 cameraAnimator
        public string animatorTrigger;    // 进入该面板的动画 Trigger（如 ToPlanet1/2/3）
        public float delay = 1.5f;        // 对应动画等待时长
    }

    [Header("从 Main 随机进入的路由集合")]
    public PanelRoute[] routes;
    public bool avoidRepeat = true; // 避免连续命中同一路由
    private int lastRouteIndex = -1;

    // ====== 键盘模拟（测试）======
    [Header("调试 / 键盘模拟")]
    public bool enableKeySimulation = true;
    public KeyCode simulateKey = KeyCode.Space; // 按此键遵循“Main→随机进入 / Panel→回主”
    public bool bypassCooldownAndChecks = false; // 测试时是否忽略冷却/新帧（默认 false 更安全）
    public KeyCode[] routeHotkeys = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4 };

    // ====== Mediapipe & 状态 ======
    private HolisticTrackingGraph trackingGraph;
    private Vector3[] landmarkPositions = new Vector3[21];
    private bool hasNewFrame;
    private bool dataUpdated;
    private int stableFrameCounter;
    private float gestureTimer;
    private bool isGestureLocked;     // 冷却锁
    private bool isPlayingAnimation;  // 动画进行中

    void Start()
    {
        StartCoroutine(InitializeSystem());
        if (soundPlayer == null) soundPlayer = GetComponent<AudioSource>();
    }

    void EnsureCurrentIsMainAtStart()
    {
        if (panelController == null || mainPlanetPanel == null) return;

        // 如果 current 还没设或不是 main，就强制指向 main
        if (panelController.current == null || panelController.current != mainPlanetPanel)
        {
            panelController.current = mainPlanetPanel;
            // 可选：确保 main 激活，其他面板隐藏（根据你的 SimplePanelSwitcher 实现来处理）
            mainPlanetPanel.SetActive(true);
            Debug.Log("[FistGesture] ✅ 强制将 current 指向 mainPlanetPanel");
        }
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
        Debug.Log("[FistGesture] ✅ 初始化完成");
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
            var p = landmarkList.Landmark[i];
            landmarkPositions[i] = new Vector3(p.X, p.Y, p.Z);
        }
        hasNewFrame = true;
        dataUpdated = true;
    }

    // ====== 键盘模拟：统一走 RequestTransition（同一帧只会进一个分支）======
    void Update()
    {
        if (!enableKeySimulation) return;

        if (Input.GetKeyDown(simulateKey))
        {
            if (bypassCooldownAndChecks)
            {
                isGestureLocked = false;
                hasNewFrame = true;
                dataUpdated = true;
            }
            RequestTransition("Key.Space");
        }

        // 数字键：直达某一路由（不改变“根据 Main/Panel 自动分支”的通用规则）
        if (routeHotkeys != null && routes != null)
        {
            for (int i = 0; i < routeHotkeys.Length && i < routes.Length; i++)
            {
                if (Input.GetKeyDown(routeHotkeys[i]))
                {
                    if (isPlayingAnimation) return;
                    StartCoroutine(ExecuteRouteByIndex(i, "Hotkey"));
                    break;
                }
            }
        }
    }

    // ====== 手势检测：命中后也只调用统一入口 ======
    void AnalyzeFistGesture()
    {
        if (isGestureLocked || !dataUpdated || !hasNewFrame || isPlayingAnimation) { dataUpdated = false; return; }

        if (DetectFist())
        {
            stableFrameCounter++;
            if (stableFrameCounter >= requiredStableFrames)
            {
                gestureTimer += 0.1f;
                if (gestureTimer >= gestureHoldDuration)
                {
                    RequestTransition("Gesture.Fist");
                }
            }
        }
        else
        {
            ResetGestureState();
        }
        dataUpdated = false;
    }

    // ====== 统一入口：在“触发那一刻”快照当前面板并上锁，保证只走一条路径 ======
    void RequestTransition(string reason)
    {
        if (isPlayingAnimation) { Debug.Log($"[FistGesture] ⏸ 忽略触发（{reason}）：动画中"); return; }
        if (isGestureLocked) { Debug.Log($"[FistGesture] ⏸ 忽略触发（{reason}）：冷却中"); return; }

        // —— 快照当前状态（极其关键）：此刻的 current 决定“进入 or 返回” —— 
        bool wasOnMain = IsOnMainPanel();
        var snapshotPanel = panelController != null ? panelController.current : null;

        // 先上锁（防止同一帧里另一路也开协程）
        isPlayingAnimation = true;
        isGestureLocked = true;
        ResetGestureState();

        Debug.Log($"[FistGesture] ▶️ 触发：{reason} | 当前面板={(snapshotPanel ? snapshotPanel.name : "null")} | wasOnMain={wasOnMain}");

        if (wasOnMain)
        {
            // Main → 随机进入子面板
            StartCoroutine(EnterRandomFromMain());
        }
        else
        {
            // 子面板 → 返回 Main
            StartCoroutine(ReturnToMain());
        }
    }

    // ====== 工具：是否在主面板 ======
    bool IsOnMainPanel()
    {
        return panelController != null &&
               panelController.current != null &&
               mainPlanetPanel != null &&
               panelController.current == mainPlanetPanel;
    }

    void ResetGestureState()
    {
        stableFrameCounter = 0;
        gestureTimer = 0f;
    }

    // ====== 握拳判定：四指 tip 在 MCP 下方（Y 更大）======
    bool DetectFist()
    {
        try
        {
            bool indexBent = (landmarkPositions[8].y > landmarkPositions[5].y);
            bool middleBent = (landmarkPositions[12].y > landmarkPositions[9].y);
            bool ringBent = (landmarkPositions[16].y > landmarkPositions[13].y);
            bool pinkyBent = (landmarkPositions[20].y > landmarkPositions[17].y);

            bool isFist = indexBent && middleBent && ringBent && pinkyBent;
            if (isFist)
                Debug.Log($"[FistGesture] 握拳检测 - 食:{indexBent}, 中:{middleBent}, 无:{ringBent}, 小:{pinkyBent}");
            return isFist;
        }
        catch { return false; }
    }

    // ====== Main → 随机进入子面板（只会触发“进入”的动画）======
    IEnumerator EnterRandomFromMain()
    {
        // 选路由
        var candidates = new System.Collections.Generic.List<int>();
        if (routes != null)
        {
            for (int i = 0; i < routes.Length; i++)
            {
                var r = routes[i];
                if (r != null && r.panel != null) candidates.Add(i);
            }
        }

        if (candidates.Count == 0)
        {
            Debug.LogWarning("[FistGesture] ⚠️ 没有可用的 routes（Main 无法进入子面板）");
            // 回滚锁，允许再次尝试
            isPlayingAnimation = false;
            isGestureLocked = false;
            yield break;
        }

        int pickIdx = candidates[Random.Range(0, candidates.Count)];
        if (avoidRepeat && candidates.Count > 1 && pickIdx == lastRouteIndex)
        {
            int retry = candidates[Random.Range(0, candidates.Count)];
            if (retry != lastRouteIndex) pickIdx = retry;
        }
        lastRouteIndex = pickIdx;

        yield return StartCoroutine(ExecuteRouteByIndex(pickIdx, "EnterFromMain"));
    }

    // ====== 子面板 → 返回 Main（只会触发“返回”的动画）======
    IEnumerator ReturnToMain()
    {
        // 1. 预热：提前激活主面板（避免切换瞬间加载空洞）
        if (mainPlanetPanel != null && !mainPlanetPanel.activeSelf)
        {
            mainPlanetPanel.SetActive(true);

            // 可选：如果主面板有 CanvasGroup，可以先透明 & 禁交互，避免提前显示
            var cg = mainPlanetPanel.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.alpha = 0f;
                cg.interactable = false;
                cg.blocksRaycasts = false;
            }

            Debug.Log("[FistGesture] 🔄 预热主面板");
        }

        // 2. 播放音效
        if (soundPlayer != null && fistGestureSound != null)
            soundPlayer.PlayOneShot(fistGestureSound);

        // 3. 播放返回动画
        if (cameraAnimator != null && !string.IsNullOrEmpty(backTriggerName))
        {
            cameraAnimator.ResetTrigger(backTriggerName);
            cameraAnimator.SetTrigger(backTriggerName);
            Debug.Log($"[FistGesture] 🎥 触发回主动画: {backTriggerName}");
        }

        // 4. 等待动画时长
        yield return new WaitForSeconds(backAnimationDelay);

        // 5. 正式切换为 current = main
        if (panelController != null && mainPlanetPanel != null)
        {
            panelController.SwitchTo(mainPlanetPanel);
            Debug.Log($"[FistGesture] 🔙 已切回主面板: {mainPlanetPanel.name}");

            // 如果用 CanvasGroup 预热，这里恢复显示 & 交互
            var cg = mainPlanetPanel.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.alpha = 1f;
                cg.interactable = true;
                cg.blocksRaycasts = true;
            }
        }
        else
        {
            Debug.LogWarning("[FistGesture] ⚠️ 回主失败：未设置 main 或 panelController");
        }

        // 6. 解锁 + 进入冷却
        isPlayingAnimation = false;
        StartCoroutine(StartCooldown());
    }


    // ====== 执行指定路由：播放进入动画 → 等待 → 切到对应面板 ======
    IEnumerator ExecuteRouteByIndex(int idx, string logPrefix)
    {
        if (routes == null || idx < 0 || idx >= routes.Length || routes[idx] == null || routes[idx].panel == null)
        {
            Debug.LogWarning($"[FistGesture] 指定路由 {idx} 不可用");
            // 回滚锁，避免卡死
            isPlayingAnimation = false;
            isGestureLocked = false;
            yield break;
        }

        var route = routes[idx];

        // 音效
        if (soundPlayer != null && fistGestureSound != null)
            soundPlayer.PlayOneShot(fistGestureSound);

        // 触发“进入”动画（仅这个 Trigger）
        Animator useAnimator = route.animatorOverride != null ? route.animatorOverride : cameraAnimator;
        if (useAnimator != null && !string.IsNullOrEmpty(route.animatorTrigger))
        {
            useAnimator.ResetTrigger(route.animatorTrigger);
            useAnimator.SetTrigger(route.animatorTrigger);
            Debug.Log($"[FistGesture] 🎬 {logPrefix} -> Trigger:{route.animatorTrigger}, Panel:{route.panel.name}, Delay:{route.delay}");
        }

        // 等对应动画时长
        float wait = route.delay > 0 ? route.delay : 1.5f;
        yield return new WaitForSeconds(wait);

        // 切换到该路由绑定的面板
        if (panelController != null && route.panel != null)
        {
            panelController.SwitchTo(route.panel);
            Debug.Log($"[FistGesture] ✅ 已切换到面板: {route.panel.name}");
        }
        else
        {
            Debug.LogWarning("[FistGesture] ⚠️ 切换失败：面板控制器或目标面板未设置");
        }

        isPlayingAnimation = false;
        StartCoroutine(StartCooldown());
    }

    // ====== 冷却 ======
    IEnumerator StartCooldown()
    {
        Debug.Log($"[FistGesture] 🕒 冷却 {cooldownSeconds} 秒");
        yield return new WaitForSeconds(cooldownSeconds);
        isGestureLocked = false;
        Debug.Log("[FistGesture] 🔄 冷却结束");
    }

    // ====== 右键菜单：快速测试（遵循同一规则）======
    [ContextMenu("测试：根据当前状态触发一次")]
    void TestOneShot()
    {
        RequestTransition("ContextMenu");
    }
}
