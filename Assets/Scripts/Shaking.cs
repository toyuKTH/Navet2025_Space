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

    [Header("Sound Hook")]
    public MIDIStageManager stageManager;

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

    // 内部
    private Vector3[] leftHandLandmarks = new Vector3[21];
    private Vector3[] rightHandLandmarks = new Vector3[21];
    private bool hasLeftHandData = false, hasRightHandData = false;

    private float _prevFiltered = -1f, _filtered = -1f, _lastTriggerTime = -999f;
    private Vector3 _baseScale, _basePos;
    private float _breathPhase;
    private Coroutine _pulseRoutine;

    const int WRIST = 0;
    const int INDEX_MCP = 5;

    void Start()
    {
        if (target == null) target = transform;
        _baseScale = target.localScale; _basePos = target.position;

        if (stageManager == null) Log("提示：stageManager 未连接（不会发声）");
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
        // 键盘测试：按 T 发一次控制 & 音符
        if (Input.GetKeyDown(KeyCode.T))
        {
            if (stageManager != null)
            {
                stageManager.ApplyControls(0.5f, 1f, 0.5f);
                stageManager.TriggerNote(60, 100, 0.25f);
                Log("按下 T：发送 controls + C4");
            }
        }

        DoIdleBreathing();

        if (hasLeftHandData && hasRightHandData)
            AnalyzeAndMaybeTrigger();
    }

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

            if (_pulseRoutine != null) StopCoroutine(_pulseRoutine);
            _pulseRoutine = StartCoroutine(DoPulseAndShake(strength));

            if (stageManager != null)
            {
                // 连续控制（滤波开合，PB等）
                float pitch01 = 0.5f;
                float timbre01 = strength; // 强度映射到音色开度
                float rate01 = 0.5f;
                stageManager.ApplyControls(pitch01, timbre01, rate01);

                // 击发音符：根据强度简单映射半音（C4..C5）
                int baseNote = 60; // C4
                int note = Mathf.Clamp(baseNote + Mathf.RoundToInt(strength * 12f), 0, 127);
                int vel = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(80f, 120f, strength)), 1, 127);
                float dur = Mathf.Lerp(0.18f, 0.35f, 1f - strength);

                stageManager.TriggerNote(note, vel, dur);
                Log($"触发！Δ={delta:0.0000} strength={strength:0.00} → note={note} vel={vel} dur={dur:0.00}");
            }
            else Log("触发但 stageManager 未连接。");
        }
    }

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

    private void TryRegisterCallbacks(HolisticTrackingSolution holisticSolution)
    {
        try
        {
            var t = holisticSolution.GetType();
            HolisticTrackingGraph graphRunner = null;

            var field = t.GetField("graphRunner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null) graphRunner = field.GetValue(holisticSolution) as HolisticTrackingGraph;
            if (graphRunner == null)
            {
                var prop = t.GetProperty("graphRunner",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop != null) graphRunner = prop.GetValue(holisticSolution) as HolisticTrackingGraph;
            }

            if (graphRunner == null) { Log("未能通过反射获取 HolisticTrackingGraph（graphRunner）。"); return; }

            graphRunner.OnLeftHandLandmarksOutput  += OnLeftHandLandmarksReceived;
            graphRunner.OnRightHandLandmarksOutput += OnRightHandLandmarksReceived;
            Log("已注册左右手 landmark 回调。");
        }
        catch (Exception e) { Debug.LogError($"[EarthShaking:{name}] 注册回调出错: {e}"); }
    }

    private void OnLeftHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        try
        {
            var packet = e.packet;
            if (packet == null) { hasLeftHandData = false; return; }
            var list = packet.Get(NormalizedLandmarkList.Parser);
            if (list == null || list.Landmark.Count < 21) { hasLeftHandData = false; return; }
            for (int i = 0; i < 21; i++) { var lm = list.Landmark[i]; leftHandLandmarks[i] = new Vector3(lm.X, lm.Y, lm.Z); }
            hasLeftHandData = true;
        }
        catch { hasLeftHandData = false; }
    }

    private void OnRightHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        try
        {
            var packet = e.packet;
            if (packet == null) { hasRightHandData = false; return; }
            var list = packet.Get(NormalizedLandmarkList.Parser);
            if (list == null || list.Landmark.Count < 21) { hasRightHandData = false; return; }
            for (int i = 0; i < 21; i++) { var lm = list.Landmark[i]; rightHandLandmarks[i] = new Vector3(lm.X, lm.Y, lm.Z); }
            hasRightHandData = true;
        }
        catch { hasRightHandData = false; }
    }

    void OnGUI()
    {
        if (!showOnGUI) return;
        GUILayout.BeginArea(new Rect(10, 10, 560, 160), GUI.skin.box);
        GUILayout.Label("EarthShaking · Visual + MIDI Trigger (no auto notes)");
        GUILayout.Label($"Hands: L {(hasLeftHandData ? "✓" : "✗")}  R {(hasRightHandData ? "✓" : "✗")}");
        GUILayout.Label($"Dist(filtered): {_filtered:0.0000}   Δ: {Mathf.Abs(_filtered - _prevFiltered):0.0000}");
        GUILayout.Label($"Threshold: {distanceChangeThreshold:0.0000}   Cooldown: {retriggerDelay:0.00}s");
        GUILayout.Label(stageManager ? "StageManager: CONNECTED" : "StageManager: <null>");
        GUILayout.EndArea();
    }
}
