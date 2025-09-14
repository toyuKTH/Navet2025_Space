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

    // ==== 仅保留：陨石风暴 ====
    public RockStormSpawner storm;

    // ==== 新增：世界健康总控（替代 trees + earth） ====
    [Header("World Health")]
    public WorldHealthCoordinator world;           // 拖你创建的 WorldHealthCoordinator

    // 可选：手势触发时是否自动切换健康状态
    public bool changeWorldOnTrigger = false;      // 默认关：只用键盘/UI 控
    public bool depleteOnTrigger = true;           // 手势触发时变坏？false=变好

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

    // rock 状态控制（保留）
    public void EnterStorm() { if (storm) storm.Activate(true); }
    public void ExitStorm() { if (storm) storm.Activate(false); }

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
        // 键盘测试：按 T 发一次控制 & 音符（保留）
        if (Input.GetKeyDown(KeyCode.T))
        {
            if (stageManager != null)
            {
                stageManager.ApplyControls(0.5f, 1f, 0.5f);
                stageManager.TriggerNote(60, 100, 0.25f);
                Log("按下 T：发送 controls + C4");
            }
        }

        // === 替换：用健康总控 ===
        if (Input.GetKeyDown(KeyCode.G))
        {         // 变好：树清空→地球恢复→再种树
            if (world) world.GoHealthy();
            Log("GoHealthy()");
        }
        if (Input.GetKeyDown(KeyCode.D))
        {         // 变坏：树清空→地球变坏（不种树）
            if (world) world.GoDepleted();
            Log("GoDepleted()");
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
                float pitch01 = 0.5f;
                float timbre01 = strength;
                float rate01 = 0.5f;
                stageManager.ApplyControls(pitch01, timbre01, rate01);

                int baseNote = 60; // C4
                int note = Mathf.Clamp(baseNote + Mathf.RoundToInt(strength * 12f), 0, 127);
                int vel = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(80f, 120f, strength)), 1, 127);
                float dur = Mathf.Lerp(0.18f, 0.35f, 1f - strength);
                stageManager.TriggerNote(note, vel, dur);

                // 陨石风暴
                EnterStorm();

                // —— 可选：手势触发就切世界健康状态 ——
                if (world && changeWorldOnTrigger)
                {
                    if (depleteOnTrigger) world.GoDepleted();
                    else world.GoHealthy();
                }
            }
            else Log("触发但 stageManager 未连接。");
        }
        else
        {
            ExitStorm();
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

    // —— Mediapipe 回调注册/接收（原样保留） ——
    private void TryRegisterCallbacks(HolisticTrackingSolution holisticSolution) { /* ...保持你原来的实现... */ }
    private void OnLeftHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e) { /* ... */ }
    private void OnRightHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e) { /* ... */ }

    void OnGUI()
    {
        if (!showOnGUI) return;
        GUILayout.BeginArea(new Rect(10, 10, 560, 160), GUI.skin.box);
        GUILayout.Label("EarthShaking · Visual + MIDI Trigger (health-controlled)");
        GUILayout.Label($"Hands: L {(hasLeftHandData ? "✓" : "✗")}  R {(hasRightHandData ? "✓" : "✗")}");
        GUILayout.Label($"Dist(filtered): {_filtered:0.0000}   Δ: {Mathf.Abs(_filtered - _prevFiltered):0.0000}");
        GUILayout.Label($"Threshold: {distanceChangeThreshold:0.0000}   Cooldown: {retriggerDelay:0.00}s");
        GUILayout.EndArea();
    }
}
