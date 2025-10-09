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
    public string mainOBTTrigger = "mainOBT";  // Main 长时间无跳转 → 提示
    public string mainOBETrigger = "mainOBE";  // Main 离开 → 结束提示
    public string planetOBTTrigger = "planetOBT"; // Planet 无动作 → 提示
    public string planetOBETrigger = "planetOBE"; // Planet 有动作 → 结束提示
    [Tooltip("Main 面板无跳转多久后触发 mainOBT")] public float mainIdleSecondsForOBT = 10f;
    [Tooltip("子面板无用户动作多久后触发 planetOBT")] public float planetNoActionSecondsForOBT = 3f;
    [Tooltip("planetOBE 触发的去抖间隔（秒）")] public float planetOBEDebounce = 0.2f;

    [Header("音效")]
    public AudioSource soundPlayer;
    public AudioClip fistGestureSound;

    [Header("世界状态（可选）")]
    public WorldHealthCoordinator world; // 若存在，则在离开 panel 时复位

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
    private bool planetIdleOBTFired = false;
    private float lastPlanetOBESentAt = -999f;

    void Start()
    {
        StartCoroutine(InitializeSystem());
        if (soundPlayer == null) soundPlayer = GetComponent<AudioSource>();
        EnsureCurrentIsMainAtStart();
        // 订阅世界动作事件
        if (world != null)
        {
            world.OnUserAction += OnWorldUserAction;
        }
        // 周期性检查 Main/Planet 的 OBT/OBE 条件
        InvokeRepeating(nameof(OBIdleTick), 0.2f, 0.2f);
        // 进入场景时：若已经在 Main，稍作延时后应用，确保 MIDI/Tracks 初始化完成
        if (IsOnMainPanel())
            Invoke(nameof(ApplyAudioForMain), 0.05f);
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
        if (IsOnMainPanel())
        {
            mainEnteredAt = Time.time;
            mainOBTFiredThisStay = false;
            planetEnteredAt = -1f;
            planetIdleOBTFired = false;
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
        if (world != null)
        {
            world.OnUserAction -= OnWorldUserAction;
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

        /// <summary>
    /// 根据当前可用 candidates 构建/补充一轮“无放回”的洗牌袋：
    /// - 将所有可用索引打乱后放入袋子；
    /// - 若 avoidRepeat==true 且袋子首位等于 lastRouteIndex，则把它换到袋子末尾，尽量避免连抽。
    /// </summary>
    void RefillShuffleBagIfNeeded(System.Collections.Generic.List<int> candidates)
    {
        // 如果当前袋子里还有属于 candidates 的元素，就不重建（保证无放回）
        bool stillValid = false;
        foreach (var i in _shuffleBag)
            if (candidates.Contains(i)) { stillValid = true; break; }
        if (stillValid) return;

        // 先用 candidates 重建袋子
        _shuffleBag.Clear();
        _shuffleBag.AddRange(candidates);

        // Fisher–Yates 随机洗牌
        for (int i = _shuffleBag.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (_shuffleBag[i], _shuffleBag[j]) = (_shuffleBag[j], _shuffleBag[i]);
        }

        // 避免首个就与上次重复（如果可选>1）
        if (avoidRepeat && _shuffleBag.Count > 1 && _shuffleBag[0] == lastRouteIndex)
        {
            // 把首位挪到末尾
            int first = _shuffleBag[0];
            _shuffleBag.RemoveAt(0);
            _shuffleBag.Add(first);
        }
    }

    /// <summary>
    /// 从洗牌袋里取一个“下一个索引”。如果袋子空，则先补一轮。
    /// （已经确保 candidates.Count>0）
    /// </summary>
    int PopNextPseudoRandomIndex(System.Collections.Generic.List<int> candidates)
    {
        // 先确保袋子是按照当前 candidates 重建过的
        RefillShuffleBagIfNeeded(candidates);

        // 如果袋子里包含无效项（比如面板在运行期被禁用了），过滤掉
        for (int k = _shuffleBag.Count - 1; k >= 0; k--)
            if (!candidates.Contains(_shuffleBag[k])) _shuffleBag.RemoveAt(k);

        // 若过滤后为空，直接重建
        if (_shuffleBag.Count == 0)
        {
            RefillShuffleBagIfNeeded(candidates);
        }

        // 取出袋子首个
        int pick = _shuffleBag[0];
        _shuffleBag.RemoveAt(0);
        return pick;
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

        int pickIdx = PopNextPseudoRandomIndex(candidates);
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
            // 在切回主面板前，复位离开的子面板状态
            TryResetWorldStateForLeavingPanel(panelController.current);
            panelController.SwitchTo(mainPlanetPanel);
            Debug.Log($"[FistGesture] 🔙 已切回主面板: {mainPlanetPanel.name}");
            ApplyAudioForMain();

            // 进入 Main：重置主界面驻留计时与 OBT 门控
            mainEnteredAt = Time.time;
            mainOBTFiredThisStay = false;
            planetEnteredAt = -1f;
            planetIdleOBTFired = false;

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

        // 若当前处于 Main → 准备离开，先触发 mainOBE
        if (IsOnMainPanel() && obAnimator != null && !string.IsNullOrEmpty(mainOBETrigger))
        {
            obAnimator.ResetTrigger(mainOBETrigger);
            obAnimator.SetTrigger(mainOBETrigger);
            mainEnteredAt = -1f; // 结束本次 Main 驻留
            mainOBTFiredThisStay = false;
        }

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
            // 在切换前，对即将离开的面板执行复位
            TryResetWorldStateForLeavingPanel(panelController.current);
            panelController.SwitchTo(route.panel);
            Debug.Log($"[FistGesture] ✅ 已切换到面板: {route.panel.name}");
            ApplyAudioForPanel(route.panel);

            // 进入子面板：重置 Planet 计时与门控
            planetEnteredAt = Time.time;
            planetIdleOBTFired = false;
            lastPlanetOBESentAt = -999f;
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
        if (obAnimator == null) return;

        if (IsOnMainPanel())
        {
            if (mainEnteredAt < 0f) { mainEnteredAt = Time.time; mainOBTFiredThisStay = false; }
            // Main 驻留超时提示（只触发一次，直到离开 Main 再复位）
            if (!mainOBTFiredThisStay && mainIdleSecondsForOBT > 0f && (Time.time - mainEnteredAt) >= mainIdleSecondsForOBT)
            {
                if (!string.IsNullOrEmpty(mainOBTTrigger))
                {
                    obAnimator.ResetTrigger(mainOBTTrigger);
                    obAnimator.SetTrigger(mainOBTTrigger);
                }
                mainOBTFiredThisStay = true;
            }

            // 重置 Planet 门控
            planetEnteredAt = -1f;
            planetIdleOBTFired = false;
            return;
        }

        // —— 子面板逻辑 ——
        if (planetEnteredAt < 0f) { planetEnteredAt = Time.time; planetIdleOBTFired = false; }

        float sinceAction;
        if (world != null)
        {
            sinceAction = Time.time - world.LastActionTime;
        }
        else
        {
            // 若无协调器，则退化为“进入子面板后的静默时长”
            sinceAction = Time.time - planetEnteredAt;
        }

        if (!planetIdleOBTFired && planetNoActionSecondsForOBT > 0f && sinceAction >= planetNoActionSecondsForOBT)
        {
            if (!string.IsNullOrEmpty(planetOBTTrigger))
            {
                obAnimator.ResetTrigger(planetOBTTrigger);
                obAnimator.SetTrigger(planetOBTTrigger);
            }
            planetIdleOBTFired = true;
        }
    }

    // 世界检测到“有动作”时（挥手/✌️等）
    void OnWorldUserAction()
    {
        if (IsOnMainPanel()) return;
        if (obAnimator == null || string.IsNullOrEmpty(planetOBETrigger)) return;
        if (Time.time - lastPlanetOBESentAt < Mathf.Max(0.05f, planetOBEDebounce)) return; // 去抖
        obAnimator.ResetTrigger(planetOBETrigger);
        obAnimator.SetTrigger(planetOBETrigger);
        lastPlanetOBESentAt = Time.time;
        planetIdleOBTFired = false; // 重新允许下次无动作再提示
    }

    // ====== 音频基线应用 ======
        void ApplyAudioForMain()
    {
        EnsureMidi7();

        // 1) 其它面板：只清空 Ambient；Shaking 基线不要在 inactive 时调用 SetBaselines 以免立刻发 CC
        if (routes != null)
        {
            foreach (var r in routes)
            {
                if (r == null || r.panel == null) continue;

                // Ambient 清零避免串音
                var ambArr = r.panel.GetComponentsInChildren<GenerativeAmbientMidi>(true);
                foreach (var amb in ambArr)
                {
                    if (amb != null) amb.SetVolume01(0f);
                }

                // ⚠️ 关键：仅当 Shaking 组件“当前激活”时才设置基线（会触发立即发 CC）
                var sArr = r.panel.GetComponentsInChildren<Shaking>(true);
                foreach (var s in sArr)
                {
                    if (s != null && s.isActiveAndEnabled)
                    {
                        s.SetBaselines(0f, 0f); // 只有真正激活的面板才需要当场回写
                    }
                    // 若面板没激活，跳过；等真正切换过去时再由 ApplyAudioForPanel() 设置
                }
            }
        }

        // 2) 主面板下的 Shaking：设置基线（会按当前状态发一次 CC）
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

        // 3) 最后写入者：直接对全局 MIDI7 发送主音量（确保最终为 0.90）
        if (midi7 != null && midi7.isActiveAndEnabled)
        {
            midi7.SendVolumeCC01(Mathf.Clamp01(midi7MainVolume01), audioFadeSeconds);
            // 4) 再加一次小延时的确认重发，防止有迟到的 SetBaselines 把它拉走
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
        // 子面板：拉低 MIDI7 基线（由该面板的 Shaking 驱动）并抬升该面板 Ambient 基线
        if (panel == null) return;
        // 立即将 MIDI7 拉至子面板基线
        EnsureMidi7();
        if (midi7 != null) midi7.SendVolumeCC01(Mathf.Clamp01(midi7OtherBase01), audioFadeSeconds);

        // 设置该面板 Shaking 的基线
        var targetShakings = panel.GetComponentsInChildren<Shaking>(true);
        foreach (var s in targetShakings)
        {
            if (s == null) continue;
            s.SetBaselines(Mathf.Clamp01(midi7OtherBase01), Mathf.Clamp01(ambientOtherBase01));
        }

        // 同时把主面板下的 Shaking 基线也设置为子面板基线，避免其把 MIDI7 拉回主音量
        if (mainPlanetPanel != null)
        {
            var mainShakings = mainPlanetPanel.GetComponentsInChildren<Shaking>(true);
            foreach (var s in mainShakings)
            {
                if (s != null) s.SetBaselines(Mathf.Clamp01(midi7OtherBase01), 0f);
            }
        }

        // 立即把该面板下的 Ambient 拉到基线（无需等待手势）
        var ambs = panel.GetComponentsInChildren<GenerativeAmbientMidi>(true);
        foreach (var amb in ambs)
        {
            if (amb != null) amb.SetVolume01(Mathf.Clamp01(ambientOtherBase01));
        }

        // 其他面板 Ambient 清零，避免串音
        if (routes != null)
        {
            foreach (var r in routes)
            {
                if (r == null || r.panel == null || r.panel == panel) continue;
                var ambArr = r.panel.GetComponentsInChildren<GenerativeAmbientMidi>(true);
                foreach (var amb in ambArr)
                {
                    if (amb != null) amb.SetVolume01(0f);
                }
                var sArr = r.panel.GetComponentsInChildren<Shaking>(true);
                foreach (var s in sArr)
                {
                    if (s != null) s.SetBaselines(0f, 0f);
                }
            }
        }
    }

        // ====== 工具：确保 MIDI7 引用 ======
    void EnsureMidi7()
    {
        if (midi7 == null)
            Debug.LogWarning("[FistGesture] ⚠️ MIDI7 未绑定，请在 Inspector 手动指定。");
    }

    // 离开某个 panel 时，尝试复位世界（树与地球颜色）。
    // 规则：如果挂有 WorldHealthCoordinator，则调用 ResetToInitial；
    // 否则尝试在该 panel 下查找 TreeSpawnerOnSphere 和 EarthColorController 执行本地复位。
    void TryResetWorldStateForLeavingPanel(GameObject leavingPanel)
    {
        if (leavingPanel == null) return;

        if (world != null)
        {
            world.ResetToInitial(true);
            return;
        }

        // 兜底方案：局部搜索并复位
        var spawners = leavingPanel.GetComponentsInChildren<TreeSpawnerOnSphere>(true);
        foreach (var sp in spawners)
        {
            if (sp != null) sp.ForceClearNow();
        }

        var earthControllers = leavingPanel.GetComponentsInChildren<EarthColorController>(true);
        foreach (var ec in earthControllers)
        {
            if (ec != null) ec.ResetImmediate(0f);
        }
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
