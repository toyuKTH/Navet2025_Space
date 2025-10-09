using System;
using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Rect = UnityEngine.Rect;   // 避免 Mediapipe.Rect / UnityEngine.Rect 歧义

public class Shaking : MonoBehaviour
{
    private float suspendWaveUntil = -999f;

    [Header("Holistic（不填会自动找名为 Solution 的对象）")]
    public HolisticTrackingSolution holistic;

    [Header("World Health（推荐优先使用）")]
    public WorldHealthCoordinator world;   // ✨ 新增：协调器引用（建议挂当前面板内的实例）

    [Header("目标对象（为空则缩放自己）")]
    public Transform target;

    [Header("缩放映射（输入=双手距离 0..1）")]
    public float scaleMin = 0.6f;
    public float scaleMax = 1.4f;

    [Header("平滑/同步")]
    [Range(0f, 0.99f)] public float distanceSmoothing = 0.85f; // 越大越平滑
    [Tooltip("左右手数据时间差最大容忍（秒），超过则认为不同步不计算")]
    public float dataMaxAge = 0.25f;
    [Tooltip("true=用掌心（腕+食指根平均），false=用手腕")]
    public bool usePalmCenter = true;

    [Header("Storm 触发（基于变化量 Δdistance）")]
    [Tooltip("当平滑后的距离变化量 |Δ| 超过该阈值时触发一次风暴")]
    public float stormChangeThreshold = 0.02f;
    [Tooltip("两次触发之间的最小间隔（秒）")]
    public float stormRetriggerDelay = 0.6f;
    [Tooltip("风暴一次触发后维持的时间（秒）")]
    public float stormHoldTime = 0.6f;
    public RockStormSpawner storm;

    // ====== 原 HEAD 的 MIDI：StageManager 连续控制 + 单条 Melody 打音 ======
    [Header("MIDI（StageManager 连续控制 + 单条 Melody 打音）")]
    public MIDIStageManager stageManager;     // 连续控制
    public bool sendContinuousToMIDI = true;
    [Range(0f, 1f)] public float midiPitch01 = 0.5f;
    [Range(0f, 1f)] public float midiRate01 = 0.5f;

    [Header("MIDI: Note（距离变化超阈值时打一记音）")]
    public SonificationMelody midiMelody;     // 仅用于 PlayNote
    public bool triggerNoteOnChange = true;
    public int baseNote = 60;                 // C4
    public int noteRange = 12;                // [baseNote .. baseNote+noteRange]
    public float noteDuration = 0.30f;        // 秒
    [Tooltip("触发 Note 的变化阈值（可与 stormChangeThreshold 相同或略小）")]
    public float noteChangeThreshold = 0.02f;

    // ====== 可选扩展：本地多轨 + Ambient 生成器（不配置则完全不影响原逻辑） ======
    [Header("🎵 可选：本地多轨 + Ambient 生成器")]
    public SonificationMelody[] tracks;                 // 本面板内需要被控制/打音的多条轨
    public GenerativeAmbientMidi ambient;               // 面板内的 AmbientGenerator
    public bool enableTracksOnActivate = true;
    public bool disableTracksOnDeactivate = true;
    public bool openTracksOnFirstTriggerOnly = false;

    [Header("可选：本地多轨缩放（叠加）")]
    [Range(0, 2)] public float localPitchScale = 1f;
    [Range(0, 2)] public float localTimbreScale = 1f;
    [Range(0, 2)] public float localRateScale = 1f;

    [Serializable]
    public struct TrackScale { [Range(0, 2)] public float pitch; [Range(0, 2)] public float timbre; [Range(0, 2)] public float rate; }
    public TrackScale[] perTrackScales = Array.Empty<TrackScale>();
    public int localTransposeSemis = 0;

    // ====== 新增：基于手部抖动的“挥手”状态 + 音量路由 ======
    [Header("MIDI7 路由（挥手降、停挥/✌️升）")]
    [Tooltip("指向输出设备名为 'MIDI7' 的 SonificationMelody（其 midiOutName= \"MIDI7\"）")]
    public SonificationMelody midi7;             // 目标：虚拟MIDI接口 MIDI7
    [Tooltip("挥手时要被增大的另一条 MIDI（在 Inspector 里拖一个 SonificationMelody）")]
    public SonificationMelody waveBoostTarget;   // 另一路，在挥手时放大

    [Header("MIDI 音量参数")]
    [Range(0f, 1f)] public float midi7Loud = 0.90f;   // 非挥手/胜利时 MIDI7 音量（大）
    [Range(0f, 1f)] public float midi7Quiet = 0.18f;  // 挥手时 MIDI7 音量（小）
    [Range(0f, 1f)] public float boostIdle = 0.30f;   // 非挥手/胜利时 另一条音量（小）
    [Range(0f, 1f)] public float boostLoud = 0.90f;   // 挥手时 另一条音量（大）
    [Tooltip("音量变化淡入淡出时长（秒）")]
    public float volumeFade = 0.15f;

    [Header("Ambient 基线与权重（用于与面板状态叠加）")]
    [Range(0f, 1f)] public float ambientBaseVolume01 = 0f;   // 面板/系统设置的基线
    [Range(0f, 1f)] public float ambientIntensityWeight = 1f; // 叠加强度（原先是 t）

    [Header("相对偏移（在基线上叠加）")]
    [Tooltip("挥手时 MIDI7 在基线上的下压量（0..1）")]
    [Range(0f, 1f)] public float waveMidi7DownDelta = 0.40f;
    [Tooltip("挥手时 Ambient 在基线上的上浮量（0..1）")]
    [Range(0f, 1f)] public float waveAmbientUpDelta = 0.25f;
    [Tooltip("Victory 时 MIDI7 在基线上的上浮量（0..1），短暂保持")]
    [Range(0f, 1f)] public float victoryMidi7UpDelta = 0.40f;
    [Tooltip("Victory 时 Ambient 在基线上的下压量（0..1），短暂保持")]
    [Range(0f, 1f)] public float victoryAmbientDownDelta = 0.30f;
    [Tooltip("Victory 偏移保持时长（秒）")]
    public float victoryHoldSeconds = 1.20f;

    [Header("挥手判定（沿用Δdistance，挂起判定一段时间）")]
    [Tooltip("当 |Δdistance| 超阈值被判定为“刚发生挥手”，在这段持续时间内都算挥手中")]
    public float waveHangTime = 0.40f;

    private float lastWaveAt = -999f;
    private bool isWaving = false;
    
    // ===== 调试 =====
    [Header("调试")]
    public bool debugGUI = true;
    public bool verboseLogs = true;
    public float summaryLogInterval = 1.0f;
    public KeyCode snapshotKey = KeyCode.L;

    // ===== 回调线程写 / 主线程读 =====
    private readonly Vector3[] leftLms = new Vector3[21];
    private readonly Vector3[] rightLms = new Vector3[21];
    private readonly object leftLock = new object();
    private readonly object rightLock = new object();
    private volatile bool leftValid = false;
    private volatile bool rightValid = false;
    private volatile bool leftNew = false;
    private volatile bool rightNew = false;

    // 主线程时间戳
    private float leftTime = -999f;
    private float rightTime = -999f;

    // 距离状态
    private float filteredDist = -1f;   // 平滑后的距离
    private float prevFilteredDist = -1f;

    // 其他状态
    private Vector3 baseScale;
    private Coroutine stormHoldCo;
    private float lastStormTrig = -999f;
    private float lastDistanceForNote = -1f;  // Note 触发比较
    private float nextSummaryAt = 0f;
    private string lastEarlyExit = "";
    private bool _tracksOpenedOnce = false;

    // 基线 / 目标管理
    private float baselineMidi7Volume01 = 0.5f; // 由外部面板切换时设置
    private float lastAppliedMidi7 = -1f;
    private float lastAppliedAmbient = -1f;
    private float victoryBoostUntil = -999f;

    // 手部关键点索引
    const int WRIST = 0;
    const int INDEX_MCP = 5;

    void Log(string s) { if (verboseLogs) Debug.Log("[Shaking] " + s); }

    // ================= 生命周期 =================
    void Start()
    {
        if (!target) target = transform;
        baseScale = target.localScale;

        EnsurePerTrackScales();
        StartCoroutine(Connect());
        StartCoroutine(Watchdog());
    }

    void OnEnable()
    {
        _tracksOpenedOnce = false;
        if (enableTracksOnActivate && tracks != null && tracks.Length > 0 && !openTracksOnFirstTriggerOnly)
            SetTracksEnabled(true);
    }

    void OnDisable()
    {
        if (disableTracksOnDeactivate && tracks != null && tracks.Length > 0)
            SetTracksEnabled(false);
    }

    void EnsurePerTrackScales()
    {
        if (tracks == null) return;
        if (perTrackScales == null || perTrackScales.Length != tracks.Length)
        {
            var arr = new TrackScale[Mathf.Max(1, tracks.Length)];
            for (int i = 0; i < arr.Length; i++) { arr[i].pitch = 1f; arr[i].timbre = 1f; arr[i].rate = 1f; }
            perTrackScales = arr;
        }
    }

    IEnumerator Connect()
    {
        yield return new WaitForSeconds(1.2f);

        if (!holistic)
        {
            var go = GameObject.Find("Solution");
            if (go) holistic = go.GetComponent<HolisticTrackingSolution>();
            Log(go ? "已找到 'Solution' 并获取 Holistic" : "未找到 'Solution'");
        }
        if (!holistic)
        {
            Log("❌ 未获取到 HolisticTrackingSolution，无法接收手数据");
            yield break;
        }

        TryRegister(holistic);
    }

    void TryRegister(HolisticTrackingSolution h)
    {
        try
        {
            var t = h.GetType();
            HolisticTrackingGraph g = null;

            var field = t.GetField("graphRunner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null) g = field.GetValue(h) as HolisticTrackingGraph;

            if (g == null)
            {
                var prop = t.GetProperty("graphRunner",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop != null) g = prop.GetValue(h) as HolisticTrackingGraph;
            }

            if (g == null) { Log("❌ 拿不到 graphRunner（可能包版本接口不同）"); return; }

            g.OnLeftHandLandmarksOutput += OnLeft;
            g.OnRightHandLandmarksOutput += OnRight;
            Log("✅ 已注册左右手 landmark 回调");
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ============== 回调线程：只缓存数据，不用 Time.* ==============
    void OnLeft(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var list = e.packet?.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { leftValid = false; return; }

        lock (leftLock)
        {
            for (int i = 0; i < 21; i++)
            {
                var lm = list.Landmark[i];
                leftLms[i] = new Vector3(lm.X, lm.Y, lm.Z);
            }
            leftValid = true;
            leftNew = true;
        }
    }

    void OnRight(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var list = e.packet?.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { rightValid = false; return; }

        lock (rightLock)
        {
            for (int i = 0; i < 21; i++)
            {
                var lm = list.Landmark[i];
                rightLms[i] = new Vector3(lm.X, lm.Y, lm.Z);
            }
            rightValid = true;
            rightNew = true;
        }
    }

    // ============== 主线程：打时间戳 + 计算缩放 + Storm/MIDI ==============
    void Update()
    {
        if (!gameObject.activeInHierarchy) return; // 不要在隐藏/非激活时写音量
        if (Input.GetKeyDown(snapshotKey)) DumpSnapshot("手动快照");

        if (leftNew) { leftTime = Time.time; leftNew = false; }
        if (rightNew) { rightTime = Time.time; rightNew = false; }

        // Victory 优先：在 Victory 短暂保持期间禁用 Shaking（避免同时触发）
        if (Time.time < victoryBoostUntil)
        {
            EarlyExit("Victory 持续窗口，暂停 Shaking");
            ResetVisualAndStorm();
            return;
        }

        // 条件不满足：回正 + Storm 关闭 + 不发 MIDI
        if (!(leftValid && rightValid))
        {
            EarlyExit("没有同时拿到左右手有效数据");
            ResetVisualAndStorm();
            return;
        }

        float dt = Mathf.Abs(leftTime - rightTime);
        if (dt > dataMaxAge)
        {
            EarlyExit($"左右手时间差过大 Δt={dt:0.000}s > {dataMaxAge:0.000}s");
            ResetVisualAndStorm();
            return;
        }

        // —— 复制 landmark 后计算 —— 
        Vector3 lp, rp;
        lock (leftLock) lp = PalmPoint(leftLms);
        lock (rightLock) rp = PalmPoint(rightLms);

        float raw = Vector2.Distance(new Vector2(lp.x, lp.y), new Vector2(rp.x, rp.y)); // 用 xy

        // 低通
        if (filteredDist < 0f) { filteredDist = raw; prevFilteredDist = raw; }
        else
        {
            prevFilteredDist = filteredDist;
            float a = 1f - Mathf.Clamp01(distanceSmoothing);
            filteredDist = Mathf.Lerp(filteredDist, raw, a);
        }

        // 距离→0..1（按镜头需要调整阈值）
        float t = Mathf.Clamp01(Mathf.InverseLerp(0.05f, 0.45f, filteredDist));

        // ===== 视觉：缩放 =====
        float s = Mathf.Lerp(scaleMin, scaleMax, t);
        if (target) target.localScale = baseScale * s;

        // Victory 门控之后、左右手有效性检查之后，马上加：
        if (Time.time < suspendWaveUntil)
        {
            EarlyExit($"wave 被暂停到 {suspendWaveUntil:0.00}");
            ResetVisualAndStorm();        // 不放风暴/不写路由
            isWaving = false;             // 强制认为非挥手
            ApplyGestureVolumes(false);   // 立即应用非挥手音量
            return;
        }


        // ===== Storm：当 Δ 超阈值时触发一次 =====
        float delta = Mathf.Abs(filteredDist - prevFilteredDist);
        if (delta > stormChangeThreshold && (Time.time - lastStormTrig) >= stormRetriggerDelay)
        {
            lastStormTrig = Time.time;

            if (storm)
            {
                storm.Activate(true);
                if (stormHoldCo != null) StopCoroutine(stormHoldCo);
                stormHoldCo = StartCoroutine(StormAutoOff(stormHoldTime));
            }

            // ✅ 联动：抖动脉冲 → 通知协调器“变坏”
            if (world == null)
            {
                // 优先在本组件上下文（所在面板）内寻找
                world = GetComponentInParent<WorldHealthCoordinator>();
                if (world == null) world = GetComponentInParent<WorldHealthCoordinator>();
            }
            if (world != null)
            {
                Log("⚡ Shaking 脉冲 → WorldHealthCoordinator.NotifyUserAction()+GoDepleted()");
                world.NotifyUserAction();
                world.GoDepleted();
            }
            lastWaveAt = Time.time; // 更新挥手时间戳
        }

        // ===== 连续控制（MIDIStageManager）=====
        if (stageManager && sendContinuousToMIDI)
        {
            stageManager.ApplyControls(midiPitch01, t, midiRate01);
        }

        // ===== 变化超过阈值触发一个 Note（可选）=====
        if (midiMelody && triggerNoteOnChange && lastDistanceForNote > 0f && Mathf.Abs(filteredDist - lastDistanceForNote) > noteChangeThreshold)
        {
            int note = Mathf.Clamp(Mathf.RoundToInt(baseNote + t * noteRange), baseNote, baseNote + noteRange);
            midiMelody.PlayNote(note, 100, noteDuration);
        }
        lastDistanceForNote = filteredDist;

        // ===== 可选扩展：把 t/Δ 同步给本地多轨 + Ambient =====
        if (tracks != null && tracks.Length > 0)
        {
            if (openTracksOnFirstTriggerOnly && !_tracksOpenedOnce && delta > stormChangeThreshold)
            {
                SetTracksEnabled(true);
                _tracksOpenedOnce = true;
            }

            float pitch01 = 0.5f;
            float timbre01 = t;
            float rate01 = 0.5f;

            ApplyControlsLocal(pitch01, timbre01, rate01);

            if (delta > noteChangeThreshold)
            {
                int n = Mathf.Clamp(baseNote + localTransposeSemis + Mathf.RoundToInt(t * noteRange), 0, 127);
                TriggerNoteLocal(n, 100, noteDuration);
            }
        }

        if (ambient != null)
        {
            // Ambient 体感映射 = 基线 + t * 权重，再叠加挥手/Victory 偏移
            float extraWaveAmbient = isWaving ? waveAmbientUpDelta : 0f;
            float extraVictoryAmbient = (Time.time < victoryBoostUntil) ? (-victoryAmbientDownDelta) : 0f;
            float ambTarget = Mathf.Clamp01(ambientBaseVolume01 + ambientIntensityWeight * t + extraWaveAmbient + extraVictoryAmbient);
            if (Mathf.Abs(ambTarget - lastAppliedAmbient) > 0.004f){
                ambient.SetVolume01(ambTarget); // ✅ 用同一条淡入淡出时长
                lastAppliedAmbient = ambTarget;
            }
            ambient.SetTempoMultiplier(0.5f + 1.5f * midiRate01);
        }

        // ===== 新增：挥手状态机（基于 lastWaveAt 的挂起时间）=====
        bool wavingNow = (Time.time - lastWaveAt) <= waveHangTime;
        if (wavingNow != isWaving)
        {
            isWaving = wavingNow;
            ApplyGestureVolumes(isWaving);
        }

        // 连续维护 MIDI7 目标（基线 ± 挥手偏移 ± Victory 短暂偏移）
        if (midi7 != null)
        {
            float extraWaveMidi7 = isWaving ? (-waveMidi7DownDelta) : 0f;
            float extraVictoryMidi7 = (Time.time < victoryBoostUntil) ? (victoryMidi7UpDelta) : 0f;
            float midi7Target = Mathf.Clamp01(baselineMidi7Volume01 + extraWaveMidi7 + extraVictoryMidi7);
            if (Mathf.Abs(midi7Target - lastAppliedMidi7) > 0.004f)
            {
                midi7.SendVolumeCC01(midi7Target, volumeFade);
                lastAppliedMidi7 = midi7Target;
            }
        }


        // 汇总日志
        if (Time.time >= nextSummaryAt)
        {
            nextSummaryAt = Time.time + Mathf.Max(0.1f, summaryLogInterval);
            Log($"UPD: raw={raw:0.000} filtered={filteredDist:0.000} Δ={delta:0.000} t={t:0.00} scale={s:0.00} Δt={dt:0.000}s");
        }

        lastEarlyExit = "";
    }

    // ================= 工具/调试 =================
    IEnumerator StormAutoOff(float sec)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, sec));
        if (storm) storm.Activate(false);
        stormHoldCo = null;
    }

    void ResetVisualAndStorm()
    {
        if (target) target.localScale = baseScale;      // 回到初始大小
        if (storm)
        {
            storm.Activate(false);                      // 关风暴
            if (stormHoldCo != null) { StopCoroutine(stormHoldCo); stormHoldCo = null; }
        }
    }

    void EarlyExit(string reason)
    {
        if (reason != lastEarlyExit) Log("早退：" + reason);
        lastEarlyExit = reason;
    }

    Vector3 PalmPoint(Vector3[] lms)
    {
        if (!usePalmCenter) return lms[WRIST];
        return (lms[WRIST] + lms[INDEX_MCP]) * 0.5f;
    }

    IEnumerator Watchdog()
    {
        float lastAny = Time.time;
        while (true)
        {
            if (leftValid || rightValid) lastAny = Time.time;
            if (Time.time - lastAny > 2f)
            {
                Log("⚠️ 2s 内没有手数据 —— 检查摄像头/光照/画面内是否有双手");
                lastAny = Time.time;
            }
            yield return new WaitForSeconds(1f);
        }
    }

    void DumpSnapshot(string tag)
    {
        Vector3 lp, rp;
        lock (leftLock) lp = PalmPoint(leftLms);
        lock (rightLock) rp = PalmPoint(rightLms);

        float dt = Mathf.Abs(leftTime - rightTime);
        Log($"[{tag}] leftValid={leftValid} tL={leftTime:0.000}  rightValid={rightValid} tR={rightTime:0.000}  Δt={dt:0.000}  " +
            $"L=({lp.x:0.000},{lp.y:0.000}) R=({rp.x:0.000},{rp.y:0.000}) filtered={filteredDist:0.000}");
    }

    void OnGUI()
    {
        if (!debugGUI) return;
        GUILayout.BeginArea(new Rect(10, 10, 560, 180), GUI.skin.box);
        GUILayout.Label("Shaking · 双手距离 → 缩放 + Storm(Δ触发) + MIDI(Stage) + 可选多轨/Ambient");
        GUILayout.Label($"leftValid={leftValid} tL={leftTime:0.00} | rightValid={rightValid} tR={rightTime:0.00}  (max Δt={dataMaxAge:0.000}s)");
        GUILayout.Label($"dist(filtered)={filteredDist:0.000}  Δ={Mathf.Abs(filteredDist - prevFilteredDist):0.000}  " +
                        $"stormTh={stormChangeThreshold:0.000}  retrig={stormRetriggerDelay:0.00}s  hold={stormHoldTime:0.00}s");
        GUILayout.EndArea();
    }

    // -------------- 可选：本地多轨实现（不配置则不生效） --------------
    void SetTracksEnabled(bool on)
    {
        foreach (var t in tracks)
        {
            if (t == null) continue;
            t.enabled = on; // 触发 SonificationMelody.OnEnable/OnDisable（会淡入/淡出）
        }
    }

    void ApplyControlsLocal(float pitch01, float timbre01, float rate01)
    {
        if (tracks == null || tracks.Length == 0) return;

        pitch01 = Mathf.Clamp01(pitch01) * Mathf.Max(0f, localPitchScale);
        timbre01 = Mathf.Clamp01(timbre01) * Mathf.Max(0f, localTimbreScale);
        rate01 = Mathf.Clamp01(rate01) * Mathf.Max(0f, localRateScale);

        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null || !t.isActiveAndEnabled) continue;

            float p = pitch01, tm = timbre01, r = rate01;
            if (perTrackScales != null && i < perTrackScales.Length)
            {
                p *= Mathf.Max(0f, perTrackScales[i].pitch);
                tm *= Mathf.Max(0f, perTrackScales[i].timbre);
                r *= Mathf.Max(0f, perTrackScales[i].rate);
            }

            t.ApplyControls(Mathf.Clamp01(p), Mathf.Clamp01(tm), Mathf.Clamp01(r));
        }
    }

    void TriggerNoteLocal(int note, int velocity = 100, float duration = 0.25f)
    {
        if (tracks == null || tracks.Length == 0) return;

        int n = Mathf.Clamp(note + localTransposeSemis, 0, 127);
        int vel = Mathf.Clamp(velocity, 1, 127);
        float dur = Mathf.Max(0.01f, duration);

        foreach (var t in tracks)
        {
            if (t == null || !t.isActiveAndEnabled) continue;
            t.PlayNote(n, vel, dur);
        }
    }

    // 把体感状态映射到 MIDI7 / waveBoostTarget 的音量
    void ApplyGestureVolumes(bool waving)
    {
        // 基线 + 挥手/胜利相对偏移
        float extraWaveMidi7 = waving ? (-waveMidi7DownDelta) : 0f;
        float extraVictoryMidi7 = (Time.time < victoryBoostUntil) ? (victoryMidi7UpDelta) : 0f;
        float midi7Target = Mathf.Clamp01(baselineMidi7Volume01 + extraWaveMidi7 + extraVictoryMidi7);

        float boostTarget = waving ? boostLoud : boostIdle;
        if (midi7 != null) midi7.SendVolumeCC01(midi7Target, volumeFade);
        if (waveBoostTarget != null) waveBoostTarget.SendVolumeCC01(boostTarget, volumeFade);
        lastAppliedMidi7 = midi7Target;

        Log($"[MIDI路由] waving={waving} baseline={baselineMidi7Volume01:0.00} midi7->{midi7Target:0.00} other->{boostTarget:0.00}");
    }

    // ✌️胜利（Peace/Victory）时，强制回到“非挥手”的大音量场景
    public void OnVictoryGesture()
    {
        // 立刻停止挥手并短暂提升 MIDI7 / 下压 Ambient
        lastWaveAt = -999f;
        isWaving = false;
        victoryBoostUntil = Time.time + Mathf.Max(0.01f, victoryHoldSeconds);
        ApplyGestureVolumes(false); // 立即按基线+Victory 偏移应用
        lastAppliedAmbient = -1f;   // 下帧强制刷新 Ambient 目标
        Log("Victory: 暂时抬升 MIDI7、下压 Ambient（基线模型）");
    }

    // ===== 外部：设置基线（由面板切换时调用） =====
    public void SetBaselines(float midi7Base01, float ambientBase01)
    {
        baselineMidi7Volume01 = Mathf.Clamp01(midi7Base01);
        ambientBaseVolume01 = Mathf.Clamp01(ambientBase01);
        lastAppliedMidi7 = -1f;
        lastAppliedAmbient = -1f;
        // 立即按当前状态应用一次（更跟手）
        ApplyGestureVolumes(isWaving);
        if (ambient != null)
        {
            float extraVictoryAmbient = (Time.time < victoryBoostUntil) ? (-victoryAmbientDownDelta) : 0f;
            float ambTarget = Mathf.Clamp01(ambientBaseVolume01 + ambientIntensityWeight * Mathf.Clamp01(filteredDist < 0f ? 0f : Mathf.InverseLerp(0.05f, 0.45f, filteredDist)) + (isWaving ? waveAmbientUpDelta : 0f) + extraVictoryAmbient);
            ambient.SetVolume01(ambTarget);
            lastAppliedAmbient = ambTarget;
        }
    }

}
