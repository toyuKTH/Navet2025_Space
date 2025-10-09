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

    [Header("音频路由（全局 MIDI7 + 面板 Ambient 基线）")]
    public SonificationMelody midi7;                 // 指向名为 "MIDI7" 输出的 SonificationMelody
    [Range(0f, 1f)] public float midi7MainVolume01 = 0.90f; // 主面板时的目标音量
    [Range(0f, 1f)] public float midi7OtherBase01 = 0.00f;  // 其他面板的基线（一般为 0）
    [Range(0f, 1f)] public float ambientOtherBase01 = 0.60f; // 子面板 Ambient 的基线
    public float audioFadeSeconds = 0.25f;                    // 基线淡入淡出

    [Header("动画（回主时使用；进入子面板由路由配置）")]
    public Animator cameraAnimator;
    public string backTriggerName = "BackToMain";
    public float backAnimationDelay = 1.5f;

    [Header("OB 提示/引导触发")]
    public Animator obAnimator;             // 专门驱动 OB 动画的 Animator（非相机）
    public string mainOBTTrigger = "mainOBT";   // Main 长时间无跳转 → 提示
    public string mainOBETrigger = "mainOBE";   // Main 离开 → 结束提示
    public string planetOBTTrigger = "planetOBT"; // Planet 无动作 → 提示
    public string planetOBETrigger = "planetOBE"; // Planet 有动作 → 结束提示
    [Tooltip("Main 面板无跳转多久后触发 mainOBT")] public float mainIdleSecondsForOBT = 10f;
    [Tooltip("子面板无用户动作多久后触发 planetOBT")] public float planetNoActionSecondsForOBT = 3f;
    [Tooltip("planetOBE 触发的去抖间隔（秒）")] public float planetOBEDebounce = 0.2f;

    [Header("OB 行为")]
    [Tooltip("仅在本次进入子面板期间触发过 planetOBT 后，才允许触发 planetOBE")]
    public bool planetOBERequiresOBT = true;
    [Tooltip("仅在本次驻留 Main 期间触发过 mainOBT 后，才允许触发 mainOBE")]
    public bool mainOBERequiresOBT = true;

    [Header("OB 动画检测（用于避免空触发 OBE）")]
    [Tooltip("OB Animator 层索引（通常为 0）")] public int obLayerIndex = 0;
    [Tooltip("标记为 PlanetOB 的状态 Tag（推荐在 Animator 中给播放提示动画的状态打上该 Tag）")]
    public string planetOBTag = "PlanetOB";
    [Tooltip("（可选）用于匹配的状态名列表（精确匹配 IsName）")]
    public string[] planetOBStateNames = System.Array.Empty<string>();

    [Header("音效")]
    public AudioSource soundPlayer;
    public AudioClip fistGestureSound;

    [Header("世界状态（可选）")]
    public WorldHealthCoordinator world; // 仅用于 Inspector 可视绑定；运行期将根据当前面板自动重绑

    [Header("自动返回 Main（无挥手/✌️超时）")]
    [Tooltip("在 Planet 内超过该秒数未检测到 Shaking/Victory（即无 World 用户动作）则自动回主；0=关闭")]
    public float autoBackToMainSeconds = 60f;

    [Header("手势检测参数")]
    public float gestureHoldDuration = 1.0f;  // 握拳保持多久触发
    public int requiredStableFrames = 3;      // 稳定帧数
    public float cooldownSeconds = 3f;        // 触发后冷却时间

    // ====== 伪随机（无放回）状态 ======
    private System.Collections.Generic.List<int> _shuffleBag = new System.Collections.Generic.List<int>();

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

    // ====== OBT/OBE 状态 ======
    private float mainEnteredAt = -1f;
    private bool mainOBTFiredThisStay = false;
    private float planetEnteredAt = -1f;
    private bool planetIdleOBTFired = false;   // 本轮 Planet 是否已触发 OBT 且尚未 OBE 清掉
    private float lastPlanetOBESentAt = -999f;
    private WorldHealthCoordinator boundWorld; // 当前面板绑定的 WorldHealthCoordinator

    // ★ 自动返回节流：下一次允许触发自动返回的最早时间戳
    private float _nextAutoBackEligibleAt = -1f; // ★

    void Start()
    {
        StartCoroutine(InitializeSystem());
        if (soundPlayer == null) soundPlayer = GetComponent<AudioSource>();
        EnsureCurrentIsMainAtStart();
        // 根据当前面板绑定协调器
        RebindWorldForPanel(IsOnMainPanel() ? mainPlanetPanel : (panelController ? panelController.current : null));
        // 周期性检查 Main/Planet 的 OBT/OBE 条件
        InvokeRepeating(nameof(OBIdleTick), 0.2f, 0.2f);
        // 进入场景时：若已经在 Main，稍作延时后应用，确保 MIDI/Tracks 初始化完成
        if (IsOnMainPanel())
            Invoke(nameof(ApplyAudioForMain), 0.05f);
    }

    void EnsureCurrentIsMainAtStart()
    {
        if (panelController == null || mainPlanetPanel == null) return;

        if (panelController.current == null || panelController.current != mainPlanetPanel)
        {
            panelController.current = mainPlanetPanel;
            mainPlanetPanel.SetActive(true);
            Debug.Log("[FistGesture] ✅ 强制将 current 指向 mainPlanetPanel");
        }
        if (IsOnMainPanel())
        {
            mainEnteredAt = Time.time;
            mainOBTFiredThisStay = false;
            planetEnteredAt = -1f;
            planetIdleOBTFired = false;
            RebindWorldForPanel(mainPlanetPanel);
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
        CancelInvoke(nameof(OBIdleTick));
        if (boundWorld != null)
        {
            boundWorld.OnUserAction -= OnWorldUserAction;
            boundWorld.OnIdleAutoHealthy -= OnWorldIdleAutoHealthy;
        }
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
                    // Planet 内：若本轮出现过 OBT，先就地补 OBE（不依赖 World）
                    if (!IsOnMainPanel() && planetIdleOBTFired &&
                        (Time.time - lastPlanetOBESentAt) >= Mathf.Max(0.05f, planetOBEDebounce) &&
                        obAnimator != null && !string.IsNullOrEmpty(planetOBETrigger))
                    {
                        obAnimator.ResetTrigger(planetOBETrigger);
                        obAnimator.SetTrigger(planetOBETrigger);
                        lastPlanetOBESentAt = Time.time;
                        planetIdleOBTFired = false;
                        Debug.Log("[FistGesture] Planet 上 fist 命中 → 立即补发 planetOBE");
                    }

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

    // ====== 统一入口 ======
    void RequestTransition(string reason)
    {
        if (isPlayingAnimation) { Debug.Log($"[FistGesture] ⏸ 忽略触发（{reason}）：动画中"); return; }
        if (isGestureLocked) { Debug.Log($"[FistGesture] ⏸ 忽略触发（{reason}）：冷却中"); return; }

        bool wasOnMain = IsOnMainPanel();
        var snapshotPanel = panelController != null ? panelController.current : null;

        isPlayingAnimation = true;
        isGestureLocked = true;
        ResetGestureState();

        Debug.Log($"[FistGesture] ▶️ 触发：{reason} | 当前面板={(snapshotPanel ? snapshotPanel.name : "null")} | wasOnMain={wasOnMain}");

        if (wasOnMain) StartCoroutine(EnterRandomFromMain());
        else StartCoroutine(ReturnToMain());
    }

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

    // ====== 握拳判定 ======
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

    // ====== Shuffle 工具 ======
    void RefillShuffleBagIfNeeded(System.Collections.Generic.List<int> candidates)
    {
        bool stillValid = false;
        foreach (var i in _shuffleBag)
            if (candidates.Contains(i)) { stillValid = true; break; }
        if (stillValid) return;

        _shuffleBag.Clear();
        _shuffleBag.AddRange(candidates);

        for (int i = _shuffleBag.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (_shuffleBag[i], _shuffleBag[j]) = (_shuffleBag[j], _shuffleBag[i]);
        }

        if (avoidRepeat && _shuffleBag.Count > 1 && _shuffleBag[0] == lastRouteIndex)
        {
            int first = _shuffleBag[0];
            _shuffleBag.RemoveAt(0);
            _shuffleBag.Add(first);
        }
    }

    int PopNextPseudoRandomIndex(System.Collections.Generic.List<int> candidates)
    {
        RefillShuffleBagIfNeeded(candidates);

        for (int k = _shuffleBag.Count - 1; k >= 0; k--)
            if (!candidates.Contains(_shuffleBag[k])) _shuffleBag.RemoveAt(k);

        if (_shuffleBag.Count == 0) RefillShuffleBagIfNeeded(candidates);

        int pick = _shuffleBag[0];
        _shuffleBag.RemoveAt(0);
        return pick;
    }

    // ====== Main → Planet ======
    IEnumerator EnterRandomFromMain()
    {
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
            isPlayingAnimation = false;
            isGestureLocked = false;
            yield break;
        }

        int pickIdx = PopNextPseudoRandomIndex(candidates);
        lastRouteIndex = pickIdx;
        yield return StartCoroutine(ExecuteRouteByIndex(pickIdx, "EnterFromMain"));
    }

    // ====== Planet → Main ======
    IEnumerator ReturnToMain()
    {
        // 记录离开时是否带着“OBT 已触发但未 OBE”
        bool needEnsureOBEAfterMain = planetIdleOBTFired;

        // 1) 预热主面板
        if (mainPlanetPanel != null && !mainPlanetPanel.activeSelf)
        {
            mainPlanetPanel.SetActive(true);
            var cg = mainPlanetPanel.GetComponent<CanvasGroup>();
            if (cg != null) { cg.alpha = 0f; cg.interactable = false; cg.blocksRaycasts = false; }
            Debug.Log("[FistGesture] 🔄 预热主面板");
        }

        // 2) 音效
        if (soundPlayer != null && fistGestureSound != null)
            soundPlayer.PlayOneShot(fistGestureSound);

        // 2.5) 离开前兜底：本轮 Planet 期间出现过 OBT → 强制补 OBE
        if (!IsOnMainPanel()
            && planetIdleOBTFired
            && (Time.time - lastPlanetOBESentAt) >= Mathf.Max(0.05f, planetOBEDebounce)
            && obAnimator != null && !string.IsNullOrEmpty(planetOBETrigger))
        {
            obAnimator.ResetTrigger(planetOBETrigger);
            obAnimator.SetTrigger(planetOBETrigger);
            lastPlanetOBESentAt = Time.time;
            planetIdleOBTFired = false;
            Debug.Log("[FistGesture] 退出子面板前强制补发 planetOBE（OBT 曾触发、尚未收尾）");
        }

        // 3) 回主动画
        if (cameraAnimator != null && !string.IsNullOrEmpty(backTriggerName))
        {
            cameraAnimator.ResetTrigger(backTriggerName);
            cameraAnimator.SetTrigger(backTriggerName);
            Debug.Log($"[FistGesture] 🎥 触发回主动画: {backTriggerName}");
        }

        // 4) 等待动画
        yield return new WaitForSeconds(backAnimationDelay);

        // 5) 切换为 Main
        if (panelController != null && mainPlanetPanel != null)
        {
            TryResetWorldStateForLeavingPanel(panelController.current);
            panelController.SwitchTo(mainPlanetPanel);
            Debug.Log($"[FistGesture] 🔙 已切回主面板: {mainPlanetPanel.name}");
            ApplyAudioForMain();

            // Main 驻留状态复位
            mainEnteredAt = Time.time;
            mainOBTFiredThisStay = false;
            planetEnteredAt = -1f;
            planetIdleOBTFired = false;

            var cg = mainPlanetPanel.GetComponent<CanvasGroup>();
            if (cg != null) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; }

            // 绑定 Main 的协调器
            RebindWorldForPanel(mainPlanetPanel);

            // 6) 新增：回到 Main 后 0.5s 再次兜底
            // 条件：若离开时 OBT 触发过但未 OBE，或 0.5s 后仍检测到 Planet 的 OB 动画在播，则补发 OBE
            StartCoroutine(EnsurePlanetOBEAfterEnterMain(0.5f, needEnsureOBEAfterMain));
        }
        else
        {
            Debug.LogWarning("[FistGesture] ⚠️ 回主失败：未设置 main 或 panelController");
        }

        // 7) 解锁 + 冷却
        isPlayingAnimation = false;
        StartCoroutine(StartCooldown());
    }

    IEnumerator EnsurePlanetOBEAfterEnterMain(float delay, bool needEnsureFromLeaving)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));
        if (!IsOnMainPanel()) yield break; // 只在 Main 执行兜底

        bool shouldSend = needEnsureFromLeaving || IsPlanetOBAnimationPlaying();
        if (shouldSend && obAnimator != null && !string.IsNullOrEmpty(planetOBETrigger))
        {
            // 去抖
            if (Time.time - lastPlanetOBESentAt < Mathf.Max(0.05f, planetOBEDebounce)) yield break;

            obAnimator.ResetTrigger(planetOBETrigger);
            obAnimator.SetTrigger(planetOBETrigger);
            lastPlanetOBESentAt = Time.time;
            planetIdleOBTFired = false;
            Debug.Log("[FistGesture] Main 安全兜底：补发 planetOBE，收掉遗留 PlanetOB 动画");
        }
    }

    // ====== 路由执行 ======
    IEnumerator ExecuteRouteByIndex(int idx, string logPrefix)
    {
        if (routes == null || idx < 0 || idx >= routes.Length || routes[idx] == null || routes[idx].panel == null)
        {
            Debug.LogWarning($"[FistGesture] 指定路由 {idx} 不可用");
            isPlayingAnimation = false;
            isGestureLocked = false;
            yield break;
        }

        var route = routes[idx];

        // 离开 Main：按门控触发 mainOBE
        if (IsOnMainPanel())
        {
            if (obAnimator != null && !string.IsNullOrEmpty(mainOBETrigger))
            {
                if (!mainOBERequiresOBT || mainOBTFiredThisStay)
                {
                    obAnimator.ResetTrigger(mainOBETrigger);
                    obAnimator.SetTrigger(mainOBETrigger);
                }
                else
                {
                    Debug.Log("[FistGesture] 跳过 mainOBE：尚未触发过 mainOBT（按配置要求）");
                }
            }
            mainEnteredAt = -1f;
            mainOBTFiredThisStay = false;
        }

        // 音效
        if (soundPlayer != null && fistGestureSound != null)
            soundPlayer.PlayOneShot(fistGestureSound);

        // 进入动画
        Animator useAnimator = route.animatorOverride != null ? route.animatorOverride : cameraAnimator;
        if (useAnimator != null && !string.IsNullOrEmpty(route.animatorTrigger))
        {
            useAnimator.ResetTrigger(route.animatorTrigger);
            useAnimator.SetTrigger(route.animatorTrigger);
            Debug.Log($"[FistGesture] 🎬 {logPrefix} -> Trigger:{route.animatorTrigger}, Panel:{route.panel.name}, Delay:{route.delay}");
        }

        // 等动画
        float wait = route.delay > 0 ? route.delay : 1.5f;
        yield return new WaitForSeconds(wait);

        // 切换面板
        if (panelController != null && route.panel != null)
        {
            TryResetWorldStateForLeavingPanel(panelController.current);
            panelController.SwitchTo(route.panel);
            Debug.Log($"[FistGesture] ✅ 已切换到面板: {route.panel.name}");
            ApplyAudioForPanel(route.panel);

            // Planet 驻留状态
            planetEnteredAt = Time.time;
            planetIdleOBTFired = false;
            lastPlanetOBESentAt = -999f;

            // 绑定该面板的 WHC，并注入子树
            RebindWorldForPanel(route.panel);

            // ★ 进入子面板：初始化最近动作时间与节流窗口，避免“秒触发”与连发
            if (boundWorld != null)
            {
                if (boundWorld.LastActionTime <= 0f) boundWorld.LastActionTime = Time.time; // ★
                else boundWorld.LastActionTime = Time.time; // ★ 直接刷新，进入即刻起算
            }
            _nextAutoBackEligibleAt = Time.time + Mathf.Max(0.5f, autoBackToMainSeconds); // ★ 首次进入给足间隔
        }
        else
        {
            Debug.LogWarning("[FistGesture] ⚠️ 切换失败：面板控制器或目标面板未设置");
        }

        isPlayingAnimation = false;
        StartCoroutine(StartCooldown());
    }

    // ====== OBT/OBE 条件检测（0.2s Tick）======
    void OBIdleTick()
{
    // 统一 0.2s tick，别在这里做耗时操作
    if (obAnimator == null) { Debug.LogWarning("[OB] obAnimator 未绑定，跳过"); return; }

    // ===== Main 面板逻辑 =====
    if (IsOnMainPanel())
    {
        if (mainEnteredAt < 0f) { mainEnteredAt = Time.time; mainOBTFiredThisStay = false; }

        float stayed = Time.time - mainEnteredAt;
        if (!mainOBTFiredThisStay && mainIdleSecondsForOBT > 0f && stayed >= mainIdleSecondsForOBT)
        {
            if (!string.IsNullOrEmpty(mainOBTTrigger))
            {
                obAnimator.ResetTrigger(mainOBTTrigger);
                obAnimator.SetTrigger(mainOBTTrigger);
                Debug.Log($"[OB] MainOBT 触发：stayed={stayed:0.00}s ≥ {mainIdleSecondsForOBT:0.00}s");
            }
            else Debug.LogWarning("[OB] MainOBTTrigger 为空，无法触发");
            mainOBTFiredThisStay = true;
        }
        else
        {
            // 可选：打开这行看门控原因
            // Debug.Log($"[OB] Main idle={stayed:0.00}/{mainIdleSecondsForOBT:0.00}, fired={mainOBTFiredThisStay}");
        }

        // Planet 相关状态在切换处重置，这里不要动 planetIdleOBTFired
        return;
    }

    // ===== Planet 面板逻辑 =====
    if (planetEnteredAt < 0f) { planetEnteredAt = Time.time; /* 本轮首次进入 */ }

    // 最近“有动作”的时间：首选 World（更准确），否则退化为进面板时间
    float sinceAction = (boundWorld != null && boundWorld.LastActionTime > 0f)
        ? Time.time - boundWorld.LastActionTime
        : Time.time - planetEnteredAt;

    // ---- 自动返回（单独受节流控制）----
    bool autoBackEligible = (_nextAutoBackEligibleAt <= 0f) || (Time.time >= _nextAutoBackEligibleAt);
    if (autoBackToMainSeconds > 0f && sinceAction >= autoBackToMainSeconds && autoBackEligible)
    {
        if (!isPlayingAnimation)
        {
            Debug.Log($"[OB] ⌛ 无动作 {sinceAction:0.00}s ≥ {autoBackToMainSeconds:0.00}s → 自动返回 Main");
            RequestTransition("AutoBackTimeout");
            _nextAutoBackEligibleAt = Time.time + autoBackToMainSeconds; // 触发后推迟下一次
        }
        return; // 自动返回已处理，本次 tick 结束
    }

    // ---- PlanetOBT（不受节流）----
    if (!planetIdleOBTFired && planetNoActionSecondsForOBT > 0f && sinceAction >= planetNoActionSecondsForOBT)
    {
        if (!string.IsNullOrEmpty(planetOBTTrigger))
        {
            obAnimator.ResetTrigger(planetOBTTrigger);
            obAnimator.SetTrigger(planetOBTTrigger);
            Debug.Log($"[OB] PlanetOBT 触发：sinceAction={sinceAction:0.00}s ≥ {planetNoActionSecondsForOBT:0.00}s");
        }
        else Debug.LogWarning("[OB] PlanetOBTTrigger 为空，无法触发");
        planetIdleOBTFired = true;
    }
    else
    {
        // 可选：打开这行看门控原因
        // Debug.Log($"[OB] Planet idle={sinceAction:0.00}/{planetNoActionSecondsForOBT:0.00}, OBTfired={planetIdleOBTFired}, autoBackEligible={autoBackEligible}");
    }
}


    // 世界检测到“有动作”
    void OnWorldUserAction()
    {
        // ★ 刷新最近动作时间 + 顺延自动返回节流窗口
        if (boundWorld != null) boundWorld.LastActionTime = Time.time; // ★
        if (autoBackToMainSeconds > 0f)
            _nextAutoBackEligibleAt = Time.time + autoBackToMainSeconds; // ★

        TriggerPlanetOBEIfOnPlanet();
    }

    // Planet 内触发 OBE（带去抖）
    void TriggerPlanetOBEIfOnPlanet()
    {
        if (IsOnMainPanel()) return;
        if (obAnimator == null || string.IsNullOrEmpty(planetOBETrigger)) return;

        if (planetOBERequiresOBT && !planetIdleOBTFired && !IsPlanetOBAnimationPlaying())
        {
            Debug.Log("[FistGesture] 跳过 planetOBE：尚未触发过 planetOBT 且动画未在播");
            return;
        }

        if (Time.time - lastPlanetOBESentAt < Mathf.Max(0.05f, planetOBEDebounce)) return;

        obAnimator.ResetTrigger(planetOBETrigger);
        obAnimator.SetTrigger(planetOBETrigger);
        lastPlanetOBESentAt = Time.time;
        planetIdleOBTFired = false;
    }

    // 检测 Planet OB 动画是否在播放（优先 Tag 其次状态名）
    bool IsPlanetOBAnimationPlaying()
    {
        if (obAnimator == null) return false;
        int layer = Mathf.Clamp(obLayerIndex, 0, obAnimator.layerCount - 1);
        var state = obAnimator.GetCurrentAnimatorStateInfo(layer);

        if (!string.IsNullOrEmpty(planetOBTag) && state.tagHash == Animator.StringToHash(planetOBTag))
            return true;

        if (planetOBStateNames != null)
        {
            for (int i = 0; i < planetOBStateNames.Length; i++)
            {
                var n = planetOBStateNames[i];
                if (!string.IsNullOrEmpty(n) && state.IsName(n)) return true;
            }
        }
        return false;
    }

    // 绑定对应的 WorldHealthCoordinator，并把它注入子树
    void RebindWorldForPanel(GameObject panel)
    {
        WorldHealthCoordinator target = null;

        if (panel != null)
            target = panel.GetComponentInChildren<WorldHealthCoordinator>(true);
        else if (world != null)
            target = world;

        if (ReferenceEquals(boundWorld, target)) return;

        if (boundWorld != null)
        {
            boundWorld.OnUserAction -= OnWorldUserAction;
            boundWorld.OnIdleAutoHealthy -= OnWorldIdleAutoHealthy;
        }

        boundWorld = target;

        if (boundWorld != null)
        {
            boundWorld.OnUserAction += OnWorldUserAction;
            boundWorld.OnIdleAutoHealthy += OnWorldIdleAutoHealthy;
            Debug.Log($"[FistGesture] 🔗 绑定 WorldHealthCoordinator: {boundWorld.name}");

            // ★ 绑定后兜底初始化 LastActionTime，避免 0 值导致“秒触发”
            if (boundWorld.LastActionTime <= 0f) boundWorld.LastActionTime = Time.time; // ★
        }
        else
        {
            Debug.LogWarning("[FistGesture] 未在当前面板找到 WorldHealthCoordinator（挥手/✌️事件将无法通过 World 回调触发 OBE）");
        }

        if (panel != null)
        {
            var shakings = panel.GetComponentsInChildren<Shaking>(true);
            foreach (var s in shakings) { s.world = boundWorld; }

            var peaces = panel.GetComponentsInChildren<PeaceSignTreeTriggerForSpawner>(true);
            foreach (var p in peaces) { p.world = boundWorld; }
        }
    }

    // —— World 空闲自动健康触发：拉起 MIDI7、下降 ambient（模拟 Victory 短暂拉起） ——
    void OnWorldIdleAutoHealthy()
    {
        // ★ 系统触发也视为一次“动作”，刷新 LastActionTime 并顺延节流
        if (boundWorld != null) boundWorld.LastActionTime = Time.time; // ★
        if (autoBackToMainSeconds > 0f)
            _nextAutoBackEligibleAt = Time.time + autoBackToMainSeconds; // ★

        var panel = panelController != null ? panelController.current : null;
        if (panel == null) panel = mainPlanetPanel;
        if (panel == null) return;

        var shakings = panel.GetComponentsInChildren<Shaking>(true);
        foreach (var s in shakings)
        {
            if (s != null && s.isActiveAndEnabled)
            {
                s.OnVictoryGesture(); // 无挥手超时也触发音频路由的“拉起/下压”
            }
        }
    }

    // ====== 音频基线应用 ======
    void ApplyAudioForMain()
    {
        EnsureMidi7();

        if (routes != null)
        {
            foreach (var r in routes)
            {
                if (r == null || r.panel == null) continue;

                var ambArr = r.panel.GetComponentsInChildren<GenerativeAmbientMidi>(true);
                foreach (var amb in ambArr) { if (amb != null) amb.SetVolume01(0f); }

                var sArr = r.panel.GetComponentsInChildren<Shaking>(true);
                foreach (var s in sArr)
                {
                    if (s != null && s.isActiveAndEnabled) { s.SetBaselines(0f, 0f); }
                }
            }
        }

        if (mainPlanetPanel != null)
        {
            var shakings = mainPlanetPanel.GetComponentsInChildren<Shaking>(true);
            foreach (var s in shakings)
            {
                if (s != null && s.isActiveAndEnabled)
                {
                    s.SetBaselines(Mathf.Clamp01(midi7MainVolume01), 0f);
                }
            }
        }

        if (midi7 != null && midi7.isActiveAndEnabled)
        {
            midi7.SendVolumeCC01(Mathf.Clamp01(midi7MainVolume01), audioFadeSeconds);
            StartCoroutine(_ConfirmMidi7After(0.15f, Mathf.Clamp01(midi7MainVolume01)));
        }
    }

    IEnumerator _ConfirmMidi7After(float sec, float v01)
    {
        yield return new WaitForSeconds(sec);
        if (midi7 != null && midi7.isActiveAndEnabled)
        {
            midi7.SendVolumeCC01(v01, 0.01f);
            Debug.Log($"[FistGesture] ✅ 确认重发 CC7={v01:0.00}");
        }
    }

    void ApplyAudioForPanel(GameObject panel)
    {
        if (panel == null) return;

        EnsureMidi7();
        if (midi7 != null) midi7.SendVolumeCC01(Mathf.Clamp01(midi7OtherBase01), audioFadeSeconds);

        var targetShakings = panel.GetComponentsInChildren<Shaking>(true);
        foreach (var s in targetShakings)
        {
            if (s == null) continue;
            s.SetBaselines(Mathf.Clamp01(midi7OtherBase01), Mathf.Clamp01(ambientOtherBase01));
        }

        if (mainPlanetPanel != null)
        {
            var mainShakings = mainPlanetPanel.GetComponentsInChildren<Shaking>(true);
            foreach (var s in mainShakings) { if (s != null) s.SetBaselines(Mathf.Clamp01(midi7OtherBase01), 0f); }
        }

        var ambs = panel.GetComponentsInChildren<GenerativeAmbientMidi>(true);
        foreach (var amb in ambs) { if (amb != null) amb.SetVolume01(Mathf.Clamp01(ambientOtherBase01)); }

        if (routes != null)
        {
            foreach (var r in routes)
            {
                if (r == null || r.panel == null || r.panel == panel) continue;

                var ambArr = r.panel.GetComponentsInChildren<GenerativeAmbientMidi>(true);
                foreach (var amb in ambArr) { if (amb != null) amb.SetVolume01(0f); }

                var sArr = r.panel.GetComponentsInChildren<Shaking>(true);
                foreach (var s in sArr) { if (s != null) s.SetBaselines(0f, 0f); }
            }
        }
    }

    // ====== 工具 ======
    void EnsureMidi7()
    {
        if (midi7 == null)
            Debug.LogWarning("[FistGesture] ⚠️ MIDI7 未绑定，请在 Inspector 手动指定。");
    }

    void TryResetWorldStateForLeavingPanel(GameObject leavingPanel)
    {
        if (leavingPanel == null) return;

        if (world != null)
        {
            world.ResetToInitial(true);
            return;
        }

        var spawners = leavingPanel.GetComponentsInChildren<TreeSpawnerOnSphere>(true);
        foreach (var sp in spawners) { if (sp != null) sp.ForceClearNow(); }

        var earthControllers = leavingPanel.GetComponentsInChildren<EarthColorController>(true);
        foreach (var ec in earthControllers) { if (ec != null) ec.ResetImmediate(0f); }
    }

    IEnumerator StartCooldown()
    {
        Debug.Log($"[FistGesture] 🕒 冷却 {cooldownSeconds} 秒");
        yield return new WaitForSeconds(cooldownSeconds);
        isGestureLocked = false;
        Debug.Log("[FistGesture] 🔄 冷却结束");
    }

    [ContextMenu("测试：根据当前状态触发一次")]
    void TestOneShot()
    {
        RequestTransition("ContextMenu");
    }
}
