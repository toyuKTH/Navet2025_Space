using System;
using System.Collections;
using UnityEngine;

using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Rect = UnityEngine.Rect;

public class HandDistanceShakeAndPulse : MonoBehaviour
{
    [Header("Debug")]
    public bool debugLogs = true;
    void Log(string m) { if (debugLogs) Debug.Log($"[EarthShaking:{name}] {m}"); }

    [Header("Mediapipe Holistic（放在名为“Solution”的对象上最稳）")]
    public HolisticTrackingSolution holistic;

    [Header("视觉目标")]
    public Transform target;

    [Header("触发判定")]
    public float distanceChangeThreshold = 0.015f;
    public float retriggerDelay = 0.60f;
    [Range(0f, 1f)] public float distanceSmoothing = 0.20f;

    [Header("视觉：idle 呼吸")]
    public bool enableIdleBreathing = true;
    public float breathingAmplitude = 0.03f;
    public float breathingSpeedHz = 0.12f;

    [Header("视觉：触发脉冲 & 抖动")]
    public float pulseScale = 1.12f;
    public float pulseDuration = 0.18f;
    public float shakeIntensity = 0.08f;
    public float shakeDuration = 0.12f;

    [Header("调试显示")]
    public bool showOnGUI = true;

    // ==== 仅保留：陨石风暴 ====
    public RockStormSpawner storm;

    // ==== 世界健康（你原来的逻辑保留） ====
    [Header("World Health")]
    public WorldHealthCoordinator world;
    public bool changeWorldOnTrigger = false;
    public bool depleteOnTrigger = true;

    // ============================
    //         本地 MIDI 控制
    // ============================
    [Header("🎵 Local MIDI / 本面板音轨")]
    public SonificationMelody[] tracks = new SonificationMelody[3];
    public GenerativeAmbientMidi ambient; // 拖入 Planet1Panel/AmbientGenerator 上的生成器


    [Tooltip("面板激活时是否启用本面板配置的轨道（将对应 SonificationMelody.enabled = true）")]
    public bool enableTracksOnActivate = true;

    [Tooltip("离开面板时是否禁用这些轨道（将对应 SonificationMelody.enabled = false）")]
    public bool disableTracksOnDeactivate = true;

    [Tooltip("（可选）仅在第一次手势触发时再打开轨道；用于“先静默，触发后开声”的演出节奏")]
    public bool openTracksOnFirstTriggerOnly = false;

    [Header("全局控制缩放（叠加到 ApplyControls）")]
    [Range(0, 2)] public float globalPitchScale = 1f;
    [Range(0, 2)] public float globalTimbreScale = 1f;
    [Range(0, 2)] public float globalRateScale = 1f;

    [Header("每轨附加缩放（叠加到全局缩放）")]
    public TrackScale[] perTrackScales = new TrackScale[6];

    [Header("音高移调（对 TriggerNote 生效）")]
    public int globalTransposeSemis = 0;

    [Serializable]
    public struct TrackScale { [Range(0, 2)] public float pitch; [Range(0, 2)] public float timbre; [Range(0, 2)] public float rate; }

    // 内部状态
    private bool _tracksOpenedByFirstTrigger = false;

    // ==== Mediapipe/内部 ====
    private Vector3[] leftHandLandmarks = new Vector3[21];
    private Vector3[] rightHandLandmarks = new Vector3[21];
    private bool hasLeftHandData = false, hasRightHandData = false;

    private float _prevFiltered = -1f, _filtered = -1f, _lastTriggerTime = -999f;
    private Vector3 _baseScale, _basePos;
    private float _breathPhase;
    private Coroutine _pulseRoutine;

    const int WRIST = 0;
    const int INDEX_MCP = 5;

    // ============================
    // 生命周期
    // ============================
    void Reset() => EnsureArrays();
    void OnValidate() => EnsureArrays();

    void EnsureArrays()
    {
        int n = Mathf.Max(1, tracks != null ? tracks.Length : 0);
        if (perTrackScales == null || perTrackScales.Length != n)
        {
            var arr = new TrackScale[n];
            for (int i = 0; i < n; i++) { arr[i].pitch = 1f; arr[i].timbre = 1f; arr[i].rate = 1f; }
            perTrackScales = arr;
        }
    }

    void OnEnable()
    {
        _tracksOpenedByFirstTrigger = false;

        // 面板激活：按配置开启轨道
        if (enableTracksOnActivate && !openTracksOnFirstTriggerOnly)
        {
            SetTracksEnabled(true);
            Log("OnEnable -> 打开本面板音轨");
        }

        if (target == null) target = transform;
        _baseScale = target.localScale; _basePos = target.position;
    }

    void OnDisable()
    {
        if (disableTracksOnDeactivate)
        {
            SetTracksEnabled(false);
            Log("OnDisable -> 关闭本面板音轨");
        }
    }

    void Start()
    {
        StartCoroutine(ConnectToHandDetection());
    }

    IEnumerator ConnectToHandDetection()
    {
        yield return new WaitForSeconds(1.2f);
        if (holistic == null)
        {
            var solution = GameObject.Find("Solution");
            if (solution != null) holistic = solution.GetComponent<HolisticTrackingSolution>();
        }
        if (holistic == null) { Log("未找到 HolisticTrackingSolution；仅保留 idle 呼吸。"); yield break; }
        TryRegisterCallbacks(holistic);
    }

    void Update()
    {
        // 示例：本地测试按键
        if (Input.GetKeyDown(KeyCode.T))
        {
            // 发一帧控制 + 音符，验证链路
            ApplyControlsLocal(0.5f, 1f, 0.5f);
            TriggerNoteLocal(60, 100, 0.25f);
            Log("按下 T：local controls + C4");
        }

        DoIdleBreathing();

        if (hasLeftHandData && hasRightHandData)
            AnalyzeAndMaybeTrigger();
    }

    // ============================
    // 本地 MIDI 控制实现
    // ============================
    void SetTracksEnabled(bool on)
    {
        if (tracks == null) return;
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null) continue;
            t.enabled = on; // 通常 SonificationMelody.enabled 控制是否发声/更新
        }
    }

    void ApplyControlsLocal(float pitch01, float timbre01, float rate01)
    {
        pitch01 = Mathf.Clamp01(pitch01);
        timbre01 = Mathf.Clamp01(timbre01);
        rate01 = Mathf.Clamp01(rate01);

        // 叠加全局缩放
        pitch01 = Mathf.Clamp01(pitch01 * Mathf.Max(0f, globalPitchScale));
        timbre01 = Mathf.Clamp01(timbre01 * Mathf.Max(0f, globalTimbreScale));
        rate01 = Mathf.Clamp01(rate01 * Mathf.Max(0f, globalRateScale));

        if (tracks == null || tracks.Length == 0) { Log("无 tracks（ApplyControlsLocal）"); return; }

        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null || !t.isActiveAndEnabled) continue;

            float p = pitch01, tm = timbre01, r = rate01;
            if (i < perTrackScales.Length)
            {
                p *= Mathf.Max(0f, perTrackScales[i].pitch);
                tm *= Mathf.Max(0f, perTrackScales[i].timbre);
                r *= Mathf.Max(0f, perTrackScales[i].rate);
            }

            p = Mathf.Clamp01(p); tm = Mathf.Clamp01(tm); r = Mathf.Clamp01(r);
            t.ApplyControls(p, tm, r);
        }
    }

    void TriggerNoteLocal(int note, int velocity = 100, float duration = 0.25f)
    {
        if (tracks == null || tracks.Length == 0) { Log("无 tracks（TriggerNoteLocal）"); return; }

        int transpose = globalTransposeSemis;
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null || !t.isActiveAndEnabled) continue;

            int n = Mathf.Clamp(note + transpose, 0, 127);
            int vel = Mathf.Clamp(velocity, 1, 127);
            float dur = Mathf.Max(0.01f, duration);

            t.PlayNote(n, vel, dur);
        }
    }

    // ============================
    // 视觉 & 手势检测
    // ============================
    private void DoIdleBreathing()
    {
        if (!enableIdleBreathing || target == null) return;
        _breathPhase += Time.deltaTime * (Mathf.PI * 2f * Mathf.Max(0f, breathingSpeedHz));
        float k = Mathf.Sin(_breathPhase) * breathingAmplitude;
        target.localScale = _baseScale * (1f + k);
    }

    private void AnalyzeAndMaybeTrigger()
    {
        Vector3 lp = ExtractPalm(leftHandLandmarks);
        Vector3 rp = ExtractPalm(rightHandLandmarks);
        float rawDistance = Vector2.Distance(new Vector2(lp.x, lp.y), new Vector2(rp.x, rp.y));

        if (_filtered < 0f) { _filtered = rawDistance; _prevFiltered = rawDistance; }
        else
        {
            _prevFiltered = _filtered;
            float a = Mathf.Clamp01(1f - distanceSmoothing);
            _filtered = Mathf.Lerp(_filtered, rawDistance, a);
        }

        float delta = Mathf.Abs(_filtered - _prevFiltered);
        float now = Time.time;

        if (delta > distanceChangeThreshold && (now - _lastTriggerTime) >= retriggerDelay)
        {
            _lastTriggerTime = now;

            float strength = Mathf.Clamp01(delta * 8f);

            // —— 首次触发时再开轨（可选）——
            if (openTracksOnFirstTriggerOnly && !_tracksOpenedByFirstTrigger)
            {
                SetTracksEnabled(true);
                _tracksOpenedByFirstTrigger = true;
                Log("First trigger -> 打开本面板音轨");
            }

            // 视觉反馈
            if (_pulseRoutine != null) StopCoroutine(_pulseRoutine);
            _pulseRoutine = StartCoroutine(DoPulseAndShake(strength));

            // 音频控制：把强度映射到 timbre / note 等
            float pitch01 = 0.5f;
            float timbre01 = strength;
            float rate01 = 0.5f;
            ApplyControlsLocal(pitch01, timbre01, rate01);

            int baseNote = 60; // C4
            int note = Mathf.Clamp(baseNote + Mathf.RoundToInt(strength * 12f), 0, 127);
            int vel = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(80f, 120f, strength)), 1, 127);
            float dur = Mathf.Lerp(0.18f, 0.35f, 1f - strength);
            TriggerNoteLocal(note, vel, dur);

            // 特效
            EnterStorm();

            // 世界健康联动（可选）
            if (world && changeWorldOnTrigger)
            {
                if (depleteOnTrigger) world.GoDepleted();
                else world.GoHealthy();
            }
        }
        else
        {
            ExitStorm();
        }
    }

    public void EnterStorm() { if (storm) storm.Activate(true); }
    public void ExitStorm() { if (storm) storm.Activate(false); }

    private IEnumerator DoPulseAndShake(float strength)
    {
        if (target == null) yield break;

        float upT = pulseDuration * 0.45f;
        float dnT = pulseDuration * 0.55f;

        Vector3 startScale = target.localScale;
        Vector3 big = _baseScale * Mathf.Lerp(1f, pulseScale, strength);

        float t = 0f;
        while (t < upT) { t += Time.deltaTime; target.localScale = Vector3.Lerp(startScale, big, t / upT); yield return null; }
        t = 0f;
        while (t < dnT) { t += Time.deltaTime; target.localScale = Vector3.Lerp(big, _baseScale, t / dnT); yield return null; }
        target.localScale = _baseScale;

        float sd = shakeDuration * strength;
        float si = shakeIntensity * strength;
        Vector3 orig = _basePos;
        t = 0f;
        while (t < sd)
        {
            t += Time.deltaTime;
            Vector3 off = new Vector3(
                UnityEngine.Random.Range(-si, si),
                UnityEngine.Random.Range(-si, si),
                UnityEngine.Random.Range(-si, si) * 0.15f);
            target.position = orig + off;
            yield return null;
        }
        target.position = orig;

        _pulseRoutine = null;
    }

    private Vector3 ExtractPalm(Vector3[] lms)
    {
        Vector3 p = lms[WRIST];
        if (p == Vector3.zero) p = lms[INDEX_MCP];
        return p;
    }

    // —— Mediapipe 回调注册/接收（保持你原实现的方式；示意写法） ——
    private void TryRegisterCallbacks(HolisticTrackingSolution holisticSolution)
    {
        var graphField = typeof(HolisticTrackingSolution).GetField("graphRunner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var graph = graphField?.GetValue(holisticSolution) as HolisticTrackingGraph;
        if (graph == null) { Log("找不到 GraphRunner"); return; }

        graph.OnLeftHandLandmarksOutput += OnLeftHandLandmarksReceived;
        graph.OnRightHandLandmarksOutput += OnRightHandLandmarksReceived;
    }

    private void OnLeftHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var list = e.packet?.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { hasLeftHandData = false; return; }
        for (int i = 0; i < 21; i++)
        {
            var lm = list.Landmark[i];
            leftHandLandmarks[i] = new Vector3(lm.X, lm.Y, lm.Z);
        }
        hasLeftHandData = true;
    }

    private void OnRightHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var list = e.packet?.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { hasRightHandData = false; return; }
        for (int i = 0; i < 21; i++)
        {
            var lm = list.Landmark[i];
            rightHandLandmarks[i] = new Vector3(lm.X, lm.Y, lm.Z);
        }
        hasRightHandData = true;
    }

    void OnGUI()
    {
        if (!showOnGUI) return;
        GUILayout.BeginArea(new Rect(10, 10, 560, 160), GUI.skin.box);
        GUILayout.Label("EarthShaking · Visual + Local MIDI");
        GUILayout.Label($"Hands: L {(hasLeftHandData ? "✓" : "✗")}  R {(hasRightHandData ? "✓" : "✗")}");
        GUILayout.Label($"Dist(filtered): {_filtered:0.0000}   Δ: {Mathf.Abs(_filtered - _prevFiltered):0.0000}");
        GUILayout.Label($"Threshold: {distanceChangeThreshold:0.0000}   Cooldown: {retriggerDelay:0.00}s");
        GUILayout.EndArea();
    }
}
