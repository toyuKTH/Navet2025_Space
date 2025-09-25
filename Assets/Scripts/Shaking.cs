using System;
using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Rect = UnityEngine.Rect;   // 避免 Mediapipe.Rect / UnityEngine.Rect 歧义

public class Shaking : MonoBehaviour
{
    [Header("Holistic（不填会自动找名为 Solution 的对象）")]
    public HolisticTrackingSolution holistic;

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
    [Range(0f, 1f)] public float midiRate01  = 0.5f;

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
    [Tooltip("面板激活时是否启用本面板轨道（SonificationMelody.enabled = true）")]
    public bool enableTracksOnActivate = true;
    [Tooltip("离开面板时是否禁用这些轨道")]
    public bool disableTracksOnDeactivate = true;
    [Tooltip("（可选）仅在第一次“Δ触发”时再打开轨道")]
    public bool openTracksOnFirstTriggerOnly = false;

    [Header("可选：本地多轨缩放（叠加）")]
    [Range(0, 2)] public float localPitchScale = 1f;
    [Range(0, 2)] public float localTimbreScale = 1f;
    [Range(0, 2)] public float localRateScale = 1f;

    [Serializable]
    public struct TrackScale { [Range(0, 2)] public float pitch; [Range(0, 2)] public float timbre; [Range(0, 2)] public float rate; }
    public TrackScale[] perTrackScales = Array.Empty<TrackScale>();
    public int localTransposeSemis = 0;

    // ===== 调试 =====
    [Header("调试")]
    public bool debugGUI = true;
    public bool verboseLogs = true;
    public float summaryLogInterval = 1.0f;
    public KeyCode snapshotKey = KeyCode.L;

    // ===== 回调线程写 / 主线程读 =====
    private readonly Vector3[] leftLms  = new Vector3[21];
    private readonly Vector3[] rightLms = new Vector3[21];
    private readonly object leftLock  = new object();
    private readonly object rightLock = new object();
    private volatile bool leftValid  = false;
    private volatile bool rightValid = false;
    private volatile bool leftNew    = false;
    private volatile bool rightNew   = false;

    // 主线程时间戳
    private float leftTime  = -999f;
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

    // 手部关键点索引
    const int WRIST = 0;
    const int INDEX_MCP = 5;

    void Log(string s) { if (verboseLogs) Debug.Log("[Shaking] " + s); }

    // ================= 生命周期 =================
    void Start()
    {
        if (!target) target = transform;
        baseScale = target.localScale;

        // 可选：修正 perTrackScales 长度
        EnsurePerTrackScales();

        StartCoroutine(Connect());
        StartCoroutine(Watchdog());
    }

    void OnEnable()
    {
        _tracksOpenedOnce = false;
        // 仅当配置了 tracks 时才动作；不影响原逻辑
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

            g.OnLeftHandLandmarksOutput  += OnLeft;
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
            leftNew   = true;
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
            rightNew   = true;
        }
    }

    // ============== 主线程：打时间戳 + 计算缩放 + Storm/MIDI ==============
    void Update()
    {
        if (Input.GetKeyDown(snapshotKey)) DumpSnapshot("手动快照");

        if (leftNew)  { leftTime  = Time.time; leftNew  = false; }
        if (rightNew) { rightTime = Time.time; rightNew = false; }

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

        // —— 复制 landmark 到临时变量后计算 —— 
        Vector3 lp, rp;
        lock (leftLock)  lp = PalmPoint(leftLms);
        lock (rightLock) rp = PalmPoint(rightLms);

        // 用 xy 归一化坐标算欧氏距离
        float raw = Vector2.Distance(new Vector2(lp.x, lp.y), new Vector2(rp.x, rp.y));

        // 低通
        if (filteredDist < 0f) { filteredDist = raw; prevFilteredDist = raw; }
        else {
            prevFilteredDist = filteredDist;
            float a = 1f - Mathf.Clamp01(distanceSmoothing);
            filteredDist = Mathf.Lerp(filteredDist, raw, a);
        }

        // 距离→0..1（按你的镜头需要调整这两个阈值）
        float t = Mathf.Clamp01(Mathf.InverseLerp(0.05f, 0.45f, filteredDist));

        // ===== 视觉：缩放 =====
        float s = Mathf.Lerp(scaleMin, scaleMax, t);
        if (target) target.localScale = baseScale * s;

        // ===== Storm：仅当变化量超过阈值时触发一次，维持 holdTime 后自动关闭 =====
        float delta = Mathf.Abs(filteredDist - prevFilteredDist);
        if (storm && delta > stormChangeThreshold && (Time.time - lastStormTrig) >= stormRetriggerDelay)
        {
            lastStormTrig = Time.time;
            storm.Activate(true);
            if (stormHoldCo != null) StopCoroutine(stormHoldCo);
            stormHoldCo = StartCoroutine(StormAutoOff(stormHoldTime));
        }

        // ===== 原逻辑：MIDIStageManager 连续控制 =====
        if (stageManager && sendContinuousToMIDI)
        {
            stageManager.ApplyControls(midiPitch01, t, midiRate01);
        }

        // ===== 原逻辑：变化超过阈值触发一个 Note（可选）=====
        if (midiMelody && triggerNoteOnChange && lastDistanceForNote > 0f && Mathf.Abs(filteredDist - lastDistanceForNote) > noteChangeThreshold)
        {
            int note = Mathf.Clamp(Mathf.RoundToInt(baseNote + t * noteRange), baseNote, baseNote + noteRange);
            midiMelody.PlayNote(note, 100, noteDuration);
        }
        lastDistanceForNote = filteredDist;

        // ===== 可选扩展：把 t/Δ 同步给本地多轨 + Ambient =====
        if (tracks != null && tracks.Length > 0)
        {
            // 首次“Δ触发”时才打开轨（可选）
            if (openTracksOnFirstTriggerOnly && !_tracksOpenedOnce && delta > stormChangeThreshold)
            {
                SetTracksEnabled(true);
                _tracksOpenedOnce = true;
            }

            // 把 t 当作 timbre（0..1），Δ 当作强度的瞬时映射
            float pitch01 = 0.5f;
            float timbre01 = t;
            float rate01 = 0.5f;

            ApplyControlsLocal(pitch01, timbre01, rate01);
            // 若你也希望本地多轨在“Δ触发”时打一记音：
            if (delta > noteChangeThreshold)
            {
                int n = Mathf.Clamp(baseNote + localTransposeSemis + Mathf.RoundToInt(t * noteRange), 0, 127);
                TriggerNoteLocal(n, 100, noteDuration);
            }
        }

        if (ambient != null)
        {
            // 手势强度 → 背景响度；速率 → 背景节奏倍率
            ambient.SetVolume01(t);
            ambient.SetTempoMultiplier(0.5f + 1.5f * midiRate01);
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
        // 保持 MIDI 静默（不发送控制）
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
        lock (leftLock)  lp = PalmPoint(leftLms);
        lock (rightLock) rp = PalmPoint(rightLms);

        float dt = Mathf.Abs(leftTime - rightTime);
        Log($"[{tag}] leftValid={leftValid} tL={leftTime:0.000}  rightValid={rightValid} tR={rightTime:0.000}  Δt={dt:0.000}  " +
            $"L=({lp.x:0.000},{lp.y:0.000}) R=({rp.x:0.000},{rp.y:0.000}) filtered={filteredDist:0.000}");
    }

    void OnGUI()
    {
        if (!debugGUI) return;
        GUILayout.BeginArea(new Rect(10, 10, 560, 180), GUI.skin.box);
        GUILayout.Label("Shaking · 双手距离 → 缩放 + Storm(Δ触发) + MIDI(Stage) + 可选本地多轨/Ambient");
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
}
