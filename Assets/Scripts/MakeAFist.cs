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
    public SonificationMelody midi7;
    [Range(0f, 1f)] public float midi7MainVolume01 = 0.90f;
    [Range(0f, 1f)] public float midi7OtherBase01 = 0.00f;
    [Range(0f, 1f)] public float ambientOtherBase01 = 0.60f;
    public float audioFadeSeconds = 0.25f;

    [Header("相机动画（回主使用；进入子面板由路由配置）")]
    public Animator cameraAnimator;
    public string backTriggerName = "BackToMain";
    public float backAnimationDelay = 1.5f; // 仅作为兜底超时参考

    [Header("Camera 状态匹配（Tag / StateName 二选一或同时）")]
    public int cameraLayerIndex = 0;
    public string camMainTag = "Cam_MainPose";
    public string[] camMainStateNames = System.Array.Empty<string>();
    public string camPlanetTag = "Cam_PlanetPose";
    public string[] routeCameraStateNames = System.Array.Empty<string>();
    public string[] cameraTriggerNames = System.Array.Empty<string>();

    [Header("OB 提示/引导（使用 obAnimator 的 OBT/OBE 触发）")]
    public Animator obAnimator;
    public string mainOBTTrigger = "mainOBT";
    public string mainOBETrigger = "mainOBE";
    public string planetOBTTrigger = "planetOBT";
    public string planetOBETrigger = "planetOBE";
    [Tooltip("Main 无跳转多久后触发 mainOBT")] public float mainIdleSecondsForOBT = 10f;
    [Tooltip("子面板无用户动作多久后触发 planetOBT")] public float planetNoActionSecondsForOBT = 3f;
    [Tooltip("planetOBE 去抖（秒）")] public float planetOBEDebounce = 0.2f;

    [Header("OB 行为")]
    [Tooltip("仅在本次进入子面板期间触发过 planetOBT 后，才允许触发 planetOBE")]
    public bool planetOBERequiresOBT = true;
    [Tooltip("仅在本次驻留 Main 期间触发过 mainOBT 后，才允许触发 mainOBE")]
    public bool mainOBERequiresOBT = true;

    [Header("OB 动画检测")]
    public int obLayerIndex = 0;
    public string planetOBTag = "PlanetOB";
    public string[] planetOBStateNames = System.Array.Empty<string>();

    [Header("OB 触发器卫生（建议配置为：mainOBT/mainOBE/planetOBT/planetOBE）")]
    public string[] obTriggerNames = System.Array.Empty<string>();

    [Header("音效")]
    public AudioSource soundPlayer;
    public AudioClip fistGestureSound;

    [Header("世界状态（可选）")]
    public WorldHealthCoordinator world;

    [Header("自动返回 Main（无挥手/✌️超时）")]
    public float autoBackToMainSeconds = 60f;

    [Header("手势检测参数")]
    public float gestureHoldDuration = 1.0f;
    public int requiredStableFrames = 3;
    public float cooldownSeconds = 3f;

    // ====== 伪随机（无放回）状态 ======
    private System.Collections.Generic.List<int> _shuffleBag = new System.Collections.Generic.List<int>();

    [System.Serializable]
    public class PanelRoute
    {
        public GameObject panel;
        public Animator animatorOverride;
        public string animatorTrigger;
        [Tooltip("历史 delay 字段现仅作兜底超时参考；实际切换由相机状态达成决定")]
        public float delay = 1.5f;
    }

    [Header("从 Main 随机进入的路由集合")]
    public PanelRoute[] routes;
    public bool avoidRepeat = true;
    private int lastRouteIndex = -1;

    [Header("调试 / 键盘模拟")]
    public bool enableKeySimulation = true;
    public KeyCode simulateKey = KeyCode.Space;
    public bool bypassCooldownAndChecks = false;
    public KeyCode[] routeHotkeys = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4 };

    // ====== Mediapipe & 状态 ======
    private HolisticTrackingGraph trackingGraph;
    private Vector3[] landmarkPositions = new Vector3[21];
    private bool hasNewFrame;
    private bool dataUpdated;
    private int stableFrameCounter;
    private float gestureTimer;
    private bool isGestureLocked;
    private bool isPlayingAnimation;

    // ====== OBT/OBE 状态 ======
    private float mainEnteredAt = -1f;
    private bool mainOBTFiredThisStay = false;
    private float planetEnteredAt = -1f;
    private bool planetIdleOBTFired = false;
    private float lastPlanetOBESentAt = -999f;
    private WorldHealthCoordinator boundWorld;

    // 自动返回节流
    private float _nextAutoBackEligibleAt = -1f;

    // 跳转事务 Token
    private long _transitionToken = 0;
    private long BeginTransitionSession() => ++_transitionToken;
    private bool IsCurrent(long token) => token == _transitionToken;

    [Header("错位自校正（Watchdog）")]
    public bool enableCameraPanelWatchdog = true;

    // ====== 新增：OB 上下文与解禁窗口 ======
    enum OBContext { Main, Planet }
    OBContext _obContext = OBContext.Main;
    float _obEnableAt = 0f; // 切换完成后短暂延时再允许 OBT

    void Start()
    {
        StartCoroutine(InitializeSystem());
        if (soundPlayer == null) soundPlayer = GetComponent<AudioSource>();
        EnsureCurrentIsMainAtStart();
        RebindWorldForPanel(IsOnMainPanel() ? mainPlanetPanel : (panelController ? panelController.current : null));
        InvokeRepeating(nameof(OBIdleTick), 0.2f, 0.2f);
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
            _obContext = OBContext.Main;
            _obEnableAt = Time.time + 0.3f;
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
                    var token = BeginTransitionSession();
                    isPlayingAnimation = true;
                    isGestureLocked = true;
                    StartCoroutine(ExecuteRouteByIndex(i, "Hotkey", token));
                    break;
                }
            }
        }
    }

    // Watchdog：相机状态 ↔ 面板一致性
    void LateUpdate()
    {
        if (!enableCameraPanelWatchdog || cameraAnimator == null || panelController == null) return;
        int layer = Mathf.Clamp(cameraLayerIndex, 0, cameraAnimator.layerCount - 1);
        var st = cameraAnimator.GetCurrentAnimatorStateInfo(layer);

        bool camAtMain = (!string.IsNullOrEmpty(camMainTag) && st.tagHash == Animator.StringToHash(camMainTag));
        if (!camAtMain && camMainStateNames != null)
        {
            for (int i = 0; i < camMainStateNames.Length; i++)
                if (!string.IsNullOrEmpty(camMainStateNames[i]) && st.IsName(camMainStateNames[i]))
                { camAtMain = true; break; }
        }

        bool isMainPanel = IsOnMainPanel();
        if (camAtMain && !isMainPanel)
        {
            Debug.LogWarning("[FistGesture] 🔧 Watchdog：相机在 MainPose 但 panel=Planet → 强制切回 Main");
            if (panelController != null && mainPlanetPanel != null)
            {
                TryResetWorldStateForLeavingPanel(panelController.current);
                panelController.SwitchTo(mainPlanetPanel);
                ApplyAudioForMain();
                RebindWorldForPanel(mainPlanetPanel);
                _obContext = OBContext.Main;
                _obEnableAt = Time.time + 0.3f;
            }
        }
    }

    // 手势检测
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
                    TrySendPlanetOBE("Fist before transition");

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

    // 统一入口
    void RequestTransition(string reason)
    {
        if (isPlayingAnimation) { Debug.Log($"[FistGesture] ⏸ 忽略触发（{reason}）：动画中"); return; }
        if (isGestureLocked) { Debug.Log($"[FistGesture] ⏸ 忽略触发（{reason}）：冷却中"); return; }

        bool wasOnMain = IsOnMainPanel();
        var snapshotPanel = panelController != null ? panelController.current : null;

        var token = BeginTransitionSession();
        isPlayingAnimation = true;
        isGestureLocked = true;
        ResetGestureState();

        Debug.Log($"[FistGesture] ▶️ 触发：{reason} | 当前面板={(snapshotPanel ? snapshotPanel.name : "null")} | wasOnMain={wasOnMain}");

        if (wasOnMain) StartCoroutine(EnterRandomFromMain(token));
        else StartCoroutine(ReturnToMain(token));
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

    // 握拳判定
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

    // Shuffle 工具
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

    // Main → Planet
    IEnumerator EnterRandomFromMain(long token)
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
        yield return StartCoroutine(ExecuteRouteByIndex(pickIdx, "EnterFromMain", token));
    }

    // Planet → Main
    IEnumerator ReturnToMain(long token)
    {
        if (!IsCurrent(token)) yield break;

        bool needEnsureOBEAfterMain = planetIdleOBTFired;

        // 预热主面板
        if (mainPlanetPanel != null && !mainPlanetPanel.activeSelf)
        {
            mainPlanetPanel.SetActive(true);
            var cg = mainPlanetPanel.GetComponent<CanvasGroup>();
            if (cg != null) { cg.alpha = 0f; cg.interactable = false; cg.blocksRaycasts = false; }
            Debug.Log("[FistGesture] 🔄 预热主面板");
        }

        // 音效
        if (soundPlayer != null && fistGestureSound != null)
            soundPlayer.PlayOneShot(fistGestureSound);

        // 离开前兜底：Planet 期间出现过 OBT → 强制补 OBE
        if (!IsOnMainPanel())
        {
            TrySendPlanetOBE("ReturnToMain pre-leave ensure", ignoreContext:true);
        }

        if (!IsCurrent(token)) yield break;

        // 回主动画（先清相机 Trigger）
        ResetAllCameraTriggers();
        if (cameraAnimator != null && !string.IsNullOrEmpty(backTriggerName))
        {
            cameraAnimator.SetTrigger(backTriggerName);
            Debug.Log($"[FistGesture] 🎥 触发回主动画: {backTriggerName}");
        }

        if (!IsCurrent(token)) yield break;

        // 等相机真正回到主视角（Tag/StateName）
        float timeout = Mathf.Max(6f, backAnimationDelay * 2f);
        yield return WaitAnimatorReached(cameraAnimator, cameraLayerIndex, camMainTag, camMainStateNames, 0.95f, timeout);

        if (!IsCurrent(token)) yield break;

        // 切换为 Main
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

            // 回到 Main 后 0.5s 再次兜底（若离开时确实触发过 OBT 但未 OBE）
            StartCoroutine(EnsurePlanetOBEAfterEnterMain(0.5f, needEnsureOBEAfterMain));
        }
        else
        {
            Debug.LogWarning("[FistGesture] ⚠️ 回主失败：未设置 main 或 panelController");
        }

        // 调整自动返主节流窗口（切换完成后再推迟）
        if (autoBackToMainSeconds > 0f)
            _nextAutoBackEligibleAt = Time.time + autoBackToMainSeconds;

        // 设置 OB 上下文与解禁窗口
        _obContext = OBContext.Main;
        _obEnableAt = Time.time + 0.3f;

        // 解锁 + 冷却
        isPlayingAnimation = false;
        StartCoroutine(StartCooldown());
    }

    IEnumerator EnsurePlanetOBEAfterEnterMain(float delay, bool needEnsureFromLeaving)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));
        if (!IsOnMainPanel()) yield break;

        bool shouldSend = needEnsureFromLeaving || IsPlanetOBAnimationPlaying();
        if (shouldSend)
        {
            TrySendPlanetOBE("Ensure after enter Main", ignoreContext:true);
        }
    }

    // 路由执行（新版：带 token）
    IEnumerator ExecuteRouteByIndex(int idx, string logPrefix, long token)
    {
        if (!IsCurrent(token)) yield break;

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
                    ResetAllOBTriggers();
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

        if (!IsCurrent(token)) yield break;

        // 音效
        if (soundPlayer != null && fistGestureSound != null)
            soundPlayer.PlayOneShot(fistGestureSound);

        // 进入动画（触发前清理相机 Trigger）
        Animator useAnimator = route.animatorOverride != null ? route.animatorOverride : cameraAnimator;
        ResetAllCameraTriggers();
        if (useAnimator != null && !string.IsNullOrEmpty(route.animatorTrigger))
        {
            useAnimator.SetTrigger(route.animatorTrigger);
            Debug.Log($"[FistGesture] 🎬 {logPrefix} -> Trigger:{route.animatorTrigger}, Panel:{route.panel.name}");
        }

        if (!IsCurrent(token)) yield break;

        // 等相机真正到位（Tag/StateName），超时兜底
        float timeout = Mathf.Max(6f, route.delay * 2f);
        yield return WaitAnimatorReached(useAnimator, cameraLayerIndex, camPlanetTag, routeCameraStateNames, 0.95f, timeout);

        if (!IsCurrent(token)) yield break;

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

            // 绑定 WHC 并注入
            RebindWorldForPanel(route.panel);
            if (boundWorld != null) boundWorld.LastActionTime = Time.time;

            _nextAutoBackEligibleAt = Time.time + Mathf.Max(0.5f, autoBackToMainSeconds);

            // 设置 OB 上下文与解禁窗口
            _obContext = OBContext.Planet;
            _obEnableAt = Time.time + 0.3f;
        }
        else
        {
            Debug.LogWarning("[FistGesture] ⚠️ 切换失败：面板控制器或目标面板未设置");
        }

        if (!IsCurrent(token)) yield break;

        isPlayingAnimation = false;
        StartCoroutine(StartCooldown());
    }

    // 兼容旧版：无 token 调用
    IEnumerator ExecuteRouteByIndex(int idx, string logPrefix)
    {
        var token = BeginTransitionSession();
        isPlayingAnimation = true;
        isGestureLocked = true;
        yield return ExecuteRouteByIndex(idx, logPrefix, token);
    }

    // 等待相机到达某个状态（Tag/StateName），并满足 normalizedTime 下限
    IEnumerator WaitAnimatorReached(Animator anim, int layer, string requiredTag, string[] requiredNames, float minNormTime = 0.0f, float timeout = 5f)
    {
        if (anim == null) yield break;
        layer = Mathf.Clamp(layer, 0, anim.layerCount - 1);
        float endAt = Time.time + Mathf.Max(0.5f, timeout);

        while (Time.time < endAt)
        {
            var st = anim.GetCurrentAnimatorStateInfo(layer);

            bool tagOK = false;
            if (!string.IsNullOrEmpty(requiredTag))
                tagOK = (st.tagHash == Animator.StringToHash(requiredTag));

            bool nameOK = false;
            if (requiredNames != null)
            {
                for (int i = 0; i < requiredNames.Length; i++)
                {
                    var n = requiredNames[i];
                    if (!string.IsNullOrEmpty(n) && st.IsName(n)) { nameOK = true; break; }
                }
            }

            if ((tagOK || nameOK) && st.normalizedTime >= minNormTime)
                yield break;

            yield return null;
        }
        Debug.LogWarning("[FistGesture] ⏳ WaitAnimatorReached 超时，按兜底继续后续流程。");
    }

    // 清理相机触发器
    void ResetAllCameraTriggers()
    {
        if (cameraAnimator == null || cameraTriggerNames == null) return;
        foreach (var t in cameraTriggerNames)
        {
            if (!string.IsNullOrEmpty(t)) cameraAnimator.ResetTrigger(t);
        }
    }

    // 清理 OB 触发器
    void ResetAllOBTriggers()
    {
        if (obAnimator == null || obTriggerNames == null) return;
        foreach (var t in obTriggerNames)
        {
            if (!string.IsNullOrEmpty(t)) obAnimator.ResetTrigger(t);
        }
    }

    // ====== OBT/OBE 条件检测（0.2s Tick）======
    void OBIdleTick()
    {
        // 切换过渡中，跳过 OB 计算，避免错面板/错上下文触发
        if (isPlayingAnimation) return;
        if (obAnimator == null) return;

        // ===== Main 面板逻辑 =====
        if (IsOnMainPanel())
        {
            if (_obContext != OBContext.Main) return; // 上下文不匹配不触发
            if (Time.time < _obEnableAt) return;      // 解禁窗口内不触发

            if (mainEnteredAt < 0f) { mainEnteredAt = Time.time; mainOBTFiredThisStay = false; }

            float stayed = Time.time - mainEnteredAt;
            if (!mainOBTFiredThisStay && mainIdleSecondsForOBT > 0f && stayed >= mainIdleSecondsForOBT)
            {
                if (!string.IsNullOrEmpty(mainOBTTrigger))
                {
                    ResetAllOBTriggers();
                    obAnimator.SetTrigger(mainOBTTrigger);
                    Debug.Log($"[OB] MainOBT 触发：stayed={stayed:0.00}s ≥ {mainIdleSecondsForOBT:0.00}s");
                }
                mainOBTFiredThisStay = true;
            }
            return;
        }

        // ===== Planet 面板逻辑 =====
        if (_obContext != OBContext.Planet) return;  // 上下文不匹配不触发
        if (Time.time < _obEnableAt) return;         // 解禁窗口内不触发
        if (planetEnteredAt < 0f) { planetEnteredAt = Time.time; }

        float sinceAction = (boundWorld != null && boundWorld.LastActionTime > 0f)
            ? Time.time - boundWorld.LastActionTime
            : Time.time - planetEnteredAt;

        // 自动返回（节流控制）
        bool autoBackEligible = (_nextAutoBackEligibleAt <= 0f) || (Time.time >= _nextAutoBackEligibleAt);
        if (autoBackToMainSeconds > 0f && sinceAction >= autoBackToMainSeconds && autoBackEligible)
        {
            if (!isPlayingAnimation)
            {
                Debug.Log($"[OB] ⌛ 无动作 {sinceAction:0.00}s ≥ {autoBackToMainSeconds:0.00}s → 自动返回 Main");
                RequestTransition("AutoBackTimeout");
                _nextAutoBackEligibleAt = Time.time + autoBackToMainSeconds;
            }
            return;
        }

        // PlanetOBT（不受节流）
        if (!planetIdleOBTFired && planetNoActionSecondsForOBT > 0f && sinceAction >= planetNoActionSecondsForOBT)
        {
            if (!string.IsNullOrEmpty(planetOBTTrigger))
            {
                ResetAllOBTriggers();
                obAnimator.SetTrigger(planetOBTTrigger);
                Debug.Log($"[OB] PlanetOBT 触发：sinceAction={sinceAction:0.00}s ≥ {planetNoActionSecondsForOBT:0.00}s");
            }
            planetIdleOBTFired = true;
        }
    }

    // 世界检测到“有动作”
    void OnWorldUserAction()
    {
        if (boundWorld != null) boundWorld.LastActionTime = Time.time;
        if (autoBackToMainSeconds > 0f)
            _nextAutoBackEligibleAt = Time.time + autoBackToMainSeconds;

        TrySendPlanetOBE("OnWorldUserAction");
    }

    // —— 统一收口的 Planet OBE 触发（可忽略上下文，仅用于清尾场景） ——
    bool TrySendPlanetOBE(string reason, bool ignoreContext = false)
    {
        // 上下文检查（正常路径需要 Planet）
        if (!ignoreContext && _obContext != OBContext.Planet) return false;

        if (IsOnMainPanel() && !ignoreContext) return false;
        if (obAnimator == null || string.IsNullOrEmpty(planetOBETrigger)) return false;

        // 若要求先有 OBT，则必须满足（或动画仍在播）
        if (planetOBERequiresOBT && !planetIdleOBTFired && !IsPlanetOBAnimationPlaying())
            return false;

        float now = Time.time;
        if (now - lastPlanetOBESentAt < Mathf.Max(0.05f, planetOBEDebounce))
            return false;

        ResetAllOBTriggers();
        obAnimator.ResetTrigger(planetOBETrigger);
        obAnimator.SetTrigger(planetOBETrigger);
        lastPlanetOBESentAt = now;
        planetIdleOBTFired = false;

        Debug.Log($"[FistGesture] PlanetOBE <- {reason}");
        return true;
    }

    // 检测 Planet OB 动画是否在播放（含过渡态）
    bool IsPlanetOBAnimationPlaying()
    {
        if (obAnimator == null) return false;
        int layer = Mathf.Clamp(obLayerIndex, 0, obAnimator.layerCount - 1);

        bool MatchState(AnimatorStateInfo st)
        {
            if (!string.IsNullOrEmpty(planetOBTag) && st.tagHash == Animator.StringToHash(planetOBTag))
                return true;
            if (planetOBStateNames != null)
            {
                for (int i = 0; i < planetOBStateNames.Length; i++)
                {
                    var n = planetOBStateNames[i];
                    if (!string.IsNullOrEmpty(n) && st.IsName(n)) return true;
                }
            }
            return false;
        }

        var cur = obAnimator.GetCurrentAnimatorStateInfo(layer);
        if (MatchState(cur)) return true;

        if (obAnimator.IsInTransition(layer))
        {
            var next = obAnimator.GetNextAnimatorStateInfo(layer);
            if (MatchState(next)) return true;
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

            if (boundWorld.LastActionTime <= 0f) boundWorld.LastActionTime = Time.time;
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
        if (boundWorld != null) boundWorld.LastActionTime = Time.time;
        if (autoBackToMainSeconds > 0f)
            _nextAutoBackEligibleAt = Time.time + autoBackToMainSeconds;

        var panel = panelController != null ? panelController.current : null;
        if (panel == null) panel = mainPlanetPanel;
        if (panel == null) return;

        var shakings = panel.GetComponentsInChildren<Shaking>(true);
        foreach (var s in shakings)
        {
            if (s != null && s.isActiveAndEnabled)
            {
                s.OnVictoryGesture();
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
            foreach (var s in mainShakings) { if (s != null) SetSafeBaselines(s, Mathf.Clamp01(midi7OtherBase01), 0f); }
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

    void SetSafeBaselines(Shaking s, float midi7v, float ambv)
    {
        if (s != null && s.isActiveAndEnabled) s.SetBaselines(midi7v, ambv);
    }

    // 工具
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
