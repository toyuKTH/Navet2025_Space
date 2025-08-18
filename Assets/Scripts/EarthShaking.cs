using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Rect = UnityEngine.Rect;

public class HandDistanceShakeAndPulse : MonoBehaviour
{
    [Header("目标与空间")]
    public Transform target;                 // 作用对象（星球）
    public bool affectLocalTransform = true; // 使用本地坐标系

    [Header("触发逻辑")]
    public float distanceChangeThreshold = 0.015f;
    public float retriggerDelay = 0.6f;
    [Range(0f, 1f)] public float distanceSmoothing = 0.85f;
    public bool scaleByDelta = false;
    public float deltaStrengthClamp = 1.4f;

    [Header("缩放脉冲")]
    public float pulseScaleAmount = 0.10f;     // 单次放大比例
    public float pulseDuration = 1.2f;         // 单次时长
    [Range(0.1f, 0.9f)] public float pulseUpPortion = 0.6f;

    [Header("抖动参数")]
    public float shakePositionAmplitude = 0.01f;
    public float shakeRotationDegrees = 0.6f;
    public float shakeFrequency = 5.0f;

    [Header("安全钳位")]
    public float minScaleMultiplier = 0.5f;    // 围绕本次基线的最小倍数
    public float maxScaleMultiplier = 3.0f;    // 围绕本次基线的最大倍数

    // ========== 舒缓随机 ==========
    [Header("常驻呼吸缩放")]
    public bool breathingEnabled = true;
    public float breathingAmplitude = 0.018f;  // ±1.8%
    public float breathingFrequency = 0.12f;   // ≈8.3s/周期

    [Header("常驻低频漂浮")]
    public bool driftEnabled = true;
    public float driftPositionAmplitude = 0.008f;
    public float driftFrequency = 0.08f;       // 慢

    [Header("偶发轻摇摆")]
    public float swayChancePerSecond = 0.10f;  // 每秒概率
    public Vector2 swayDurationRange = new Vector2(1.6f, 2.4f);
    public float swayAngleDegrees = 1.0f;      // ±角度
    public float swayFrequency = 0.25f;

    [Header("偶发深呼吸（更大更慢的一次脉冲）")]
    public float deepPulseChance = 0.18f;
    public float deepPulseScaleMultiplier = 1.5f;
    public float deepPulseDurationMultiplier = 1.5f;

    [Header("调试")]
    public bool showDebugInfo = true;
    public bool showDetailedLog = false;

    // —— 手部数据
    private Vector3[] leftHandLandmarks = new Vector3[21];
    private Vector3[] rightHandLandmarks = new Vector3[21];
    private bool hasLeftHandData = false;
    private bool hasRightHandData = false;
    private bool landmarksUpdated = false;

    // —— 触发控制
    private float previousFilteredDistance = 0f;
    private float filteredDistance = 0f;
    private bool isFirstFrame = true;
    private float lastTriggerTime = -999f;

    // —— 基线
    private Vector3 baseLocalPos, baseWorldPos;
    private Quaternion baseLocalRot, baseWorldRot;

    // —— 脉冲、偏移记录
    private Coroutine currentPulseRoutine;

    // 上一帧“抖动”偏移
    private Quaternion lastShakeRotOffset = Quaternion.identity;
    private Vector3   lastShakePosOffset  = Vector3.zero;

    // 上一帧“随机环境”偏移
    private Quaternion lastAmbientRotOffset = Quaternion.identity;
    private Vector3   lastAmbientPosOffset  = Vector3.zero;

    // 呼吸缩放
    private float ambientScaleFactor = 1f;          // 本帧呼吸缩放因子
    private Vector3 ambientScaleBaseline;           // 无脉冲时的缩放基线
    private Vector3 lastAmbientScaleApplied;        

    // 偶发摇摆协程
    private Coroutine swayRoutine;
    private Quaternion ambientSwayRotOffset = Quaternion.identity;

    void Awake()
    {
        if (target == null) target = transform;

        baseLocalPos = target.localPosition;
        baseWorldPos = target.position;
        baseLocalRot = target.localRotation;
        baseWorldRot = target.rotation;

        ambientScaleBaseline = target.localScale;
        lastAmbientScaleApplied = target.localScale;
    }

    void Start()
    {
        StartCoroutine(ConnectToHandDetection());
    }

    void Update()
    {
        // 更新随机环境状态
        UpdateAmbientBreathing();
        UpdateAmbientDrift();   // 兼容占位（不做事），避免编译错误
        MaybeStartSway();

        // 无脉冲时，仅应用环境偏移与呼吸缩放
        if (currentPulseRoutine == null)
        {
            ApplyOffsets(
                ambientPosOffset: ComputeAmbientPosOffset(),
                ambientRotOffset: ComputeAmbientRotOffset(),
                shakePosOffset: Vector3.zero,
                shakeRotOffset: Quaternion.identity
            );

            Vector3 targetScale = ambientScaleBaseline * ambientScaleFactor;
            target.localScale = ClampScaleAroundBaseline(ambientScaleBaseline, targetScale);
            lastAmbientScaleApplied = target.localScale;
        }
    }

    IEnumerator ConnectToHandDetection()
    {
        yield return new WaitForSeconds(2f);
        TryConnectToHolisticSystem();
        InvokeRepeating(nameof(AnalyzeHandGestures), 0.1f, 0.1f);
    }

    void TryConnectToHolisticSystem()
    {
        var solution = GameObject.Find("Solution");
        if (solution == null)
        {
            Debug.LogWarning("未找到名为 Solution 的对象");
            return;
        }

        var holisticSolution = solution.GetComponent<Mediapipe.Unity.Sample.Holistic.HolisticTrackingSolution>();
        if (holisticSolution == null)
        {
            Debug.LogError("Solution 上未找到 HolisticTrackingSolution 组件");
            return;
        }

        TryRegisterCallbacks(holisticSolution);
    }

    void TryRegisterCallbacks(Mediapipe.Unity.Sample.Holistic.HolisticTrackingSolution holisticSolution)
    {
        try
        {
            var field = holisticSolution.GetType().GetField("graphRunner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Mediapipe.Unity.Sample.Holistic.HolisticTrackingGraph graphRunner = null;

            if (field != null)
            {
                graphRunner = field.GetValue(holisticSolution) as Mediapipe.Unity.Sample.Holistic.HolisticTrackingGraph;
            }
            else
            {
                var prop = holisticSolution.GetType().GetProperty("graphRunner",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (prop != null)
                    graphRunner = prop.GetValue(holisticSolution) as Mediapipe.Unity.Sample.Holistic.HolisticTrackingGraph;
            }

            if (graphRunner == null)
            {
                Debug.LogWarning("未能通过反射获取 HolisticTrackingGraph");
                return;
            }

            graphRunner.OnLeftHandLandmarksOutput += OnLeftHandLandmarksReceived;
            graphRunner.OnRightHandLandmarksOutput += OnRightHandLandmarksReceived;
            Debug.Log("已注册左右手关键点回调");
        }
        catch (System.Exception e)
        {
            Debug.LogError("注册回调出错: " + e.Message);
        }
    }

    // —— 回调
    void OnLeftHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var packet = e.packet;
        if (packet == null) return;
        var landmarks = packet.Get(NormalizedLandmarkList.Parser);
        if (landmarks == null || landmarks.Landmark.Count < 21) { hasLeftHandData = false; return; }

        for (int i = 0; i < 21; i++)
        {
            var lm = landmarks.Landmark[i];
            leftHandLandmarks[i] = new Vector3(lm.X, lm.Y, lm.Z);
        }
        hasLeftHandData = true;
        landmarksUpdated = true;
    }

    void OnRightHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var packet = e.packet;
        if (packet == null) return;
        var landmarks = packet.Get(NormalizedLandmarkList.Parser);
        if (landmarks == null || landmarks.Landmark.Count < 21) { hasRightHandData = false; return; }

        for (int i = 0; i < 21; i++)
        {
            var lm = landmarks.Landmark[i];
            rightHandLandmarks[i] = new Vector3(lm.X, lm.Y, lm.Z);
        }
        hasRightHandData = true;
        landmarksUpdated = true;
    }

    void AnalyzeHandGestures()
    {
        if (!landmarksUpdated) return;

        if (hasLeftHandData && hasRightHandData)
        {
            Vector3 leftPalm = leftHandLandmarks[0];
            Vector3 rightPalm = rightHandLandmarks[0];

            float rawDistance = Vector3.Distance(leftPalm, rightPalm);

            if (isFirstFrame)
            {
                filteredDistance = rawDistance;
                previousFilteredDistance = filteredDistance;
                isFirstFrame = false;
                landmarksUpdated = false;
                return;
            }

            filteredDistance = Mathf.Lerp(rawDistance, previousFilteredDistance, distanceSmoothing);
            float delta = Mathf.Abs(filteredDistance - previousFilteredDistance);

            if (showDetailedLog)
                Debug.Log($"手距 raw {rawDistance:F4} filtered {filteredDistance:F4} Δ {delta:F4}");

            float now = Time.time;
            if (delta > distanceChangeThreshold && now - lastTriggerTime >= retriggerDelay)
            {
                lastTriggerTime = now;

                float strengthFactor = scaleByDelta
                    ? Mathf.Clamp(delta / distanceChangeThreshold, 0.5f, deltaStrengthClamp)
                    : 1f;

                bool deep = Random.value < deepPulseChance;

                if (currentPulseRoutine == null)
                    currentPulseRoutine = StartCoroutine(DoPulseAndShake(
                        strengthFactor,
                        deep ? deepPulseScaleMultiplier : 1f,
                        deep ? deepPulseDurationMultiplier : 1f
                    ));
            }

            previousFilteredDistance = filteredDistance;
        }

        landmarksUpdated = false;
    }

    IEnumerator DoPulseAndShake(float strength, float scaleMul = 1f, float durationMul = 1f)
    {
        if (target == null) { currentPulseRoutine = null; yield break; }

        // 以“当前缩放”为本次脉冲的基线，结束时回到它
        Vector3 startScale = target.localScale;

        float upTime   = pulseDuration * durationMul * Mathf.Clamp01(pulseUpPortion);
        float downTime = Mathf.Max(0.01f, pulseDuration * durationMul - upTime);
        float ampScale = pulseScaleAmount * strength * scaleMul;

        float posAmp   = shakePositionAmplitude * strength;
        float rotAmp   = shakeRotationDegrees * strength;

        float t = 0f;
        // 上升段
        while (t < upTime)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / upTime);
            float ease = EaseOutCubic(u);

            // 缩放 = 脉冲 * 呼吸
            Vector3 targetScale = startScale * (1f + ampScale * ease) * ambientScaleFactor;
            target.localScale = ClampScaleAroundBaseline(startScale, targetScale);

            // 抖动 + 环境偏移一起应用
            ApplyOffsets(
                ambientPosOffset: ComputeAmbientPosOffset(),
                ambientRotOffset: ComputeAmbientRotOffset(),
                shakePosOffset:   ComputeShakePosOffset(posAmp, Time.time * shakeFrequency),
                shakeRotOffset:   ComputeShakeRotOffset(rotAmp, Time.time * shakeFrequency)
            );

            yield return null;
        }

        // 回落段
        t = 0f;
        while (t < downTime)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / downTime);
            float ease = EaseInCubic(u);

            Vector3 targetScale = Vector3.Lerp(startScale * (1f + ampScale), startScale, ease) * ambientScaleFactor;
            target.localScale = ClampScaleAroundBaseline(startScale, targetScale);

            ApplyOffsets(
                ambientPosOffset: ComputeAmbientPosOffset(),
                ambientRotOffset: ComputeAmbientRotOffset(),
                shakePosOffset:   ComputeShakePosOffset(posAmp * (1f - u), Time.time * shakeFrequency),
                shakeRotOffset:   ComputeShakeRotOffset(rotAmp * (1f - u), Time.time * shakeFrequency)
            );

            yield return null;
        }

        // 结束：回到基线缩放，清空“抖动偏移”，保留环境偏移；更新呼吸缩放基线
        lastShakeRotOffset = Quaternion.identity;
        lastShakePosOffset = Vector3.zero;
        target.localScale = startScale;                 // 回到本次脉冲基线
        ambientScaleBaseline = startScale;              // 将基线更新为当前缩放
        currentPulseRoutine = null;
    }

    // —— 计算环境与抖动的偏移
    Vector3 ComputeAmbientPosOffset()
    {
        if (!driftEnabled) return Vector3.zero;
        float w = Mathf.PI * 2f * Mathf.Max(0.01f, driftFrequency);
        float dx = Mathf.Sin(Time.time * w) * driftPositionAmplitude;
        float dy = Mathf.Sin(Time.time * w * 0.77f + 1.3f) * driftPositionAmplitude * 0.8f;
        float dz = Mathf.Sin(Time.time * w * 1.11f + 2.1f) * driftPositionAmplitude * 0.6f;
        return new Vector3(dx, dy, dz);
    }

    Quaternion ComputeAmbientRotOffset()
    {
        return ambientSwayRotOffset;
    }

    Vector3 ComputeShakePosOffset(float posAmp, float timeSeed)
    {
        float nx = Mathf.PerlinNoise(timeSeed, 0.37f) - 0.5f;
        float ny = Mathf.PerlinNoise(0.73f, timeSeed) - 0.5f;
        float nz = Mathf.PerlinNoise(timeSeed * 0.7f, timeSeed * 1.3f) - 0.5f;
        return new Vector3(nx, ny, nz) * posAmp;
    }

    Quaternion ComputeShakeRotOffset(float rotAmpDeg, float timeSeed)
    {
        Vector3 euler = new Vector3(
            (Mathf.PerlinNoise(timeSeed * 0.9f, 1.11f) - 0.5f) * rotAmpDeg,
            (Mathf.PerlinNoise(2.22f, timeSeed * 1.1f) - 0.5f) * rotAmpDeg,
            (Mathf.PerlinNoise(timeSeed * 1.3f, 3.33f) - 0.5f) * rotAmpDeg
        );
        return Quaternion.Euler(euler);
    }

    // —— 兼容占位：之前版本在 Update() 调用的函数，现在漂浮用 ComputeAmbientPosOffset() 动态计算
    void UpdateAmbientDrift() { /* 保留空实现以兼容旧调用 */ }

    // —— 将“环境偏移 + 抖动偏移”叠加到“当前姿态”，并剥离上一帧的两种偏移
    void ApplyOffsets(Vector3 ambientPosOffset, Quaternion ambientRotOffset, Vector3 shakePosOffset, Quaternion shakeRotOffset)
    {
        Vector3 curPos = affectLocalTransform ? target.localPosition : target.position;
        Quaternion curRot = affectLocalTransform ? target.localRotation : target.rotation;

        // 剥离上一帧的偏移（应用顺序：Ambient 再 Shake；剥离时逆序）
        Vector3 basePos = curPos - lastShakePosOffset - lastAmbientPosOffset;
        Quaternion baseRot = curRot * Quaternion.Inverse(lastShakeRotOffset) * Quaternion.Inverse(lastAmbientRotOffset);

        Vector3 finalPos = basePos + ambientPosOffset + shakePosOffset;
        Quaternion finalRot = baseRot * ambientRotOffset * shakeRotOffset;

        if (affectLocalTransform)
        {
            target.localPosition = finalPos;
            target.localRotation = finalRot;
        }
        else
        {
            target.position = finalPos;
            target.rotation = finalRot;
        }

        lastAmbientPosOffset = ambientPosOffset;
        lastAmbientRotOffset = ambientRotOffset;
        lastShakePosOffset   = shakePosOffset;
        lastShakeRotOffset   = shakeRotOffset;
    }

    // —— 呼吸缩放
    void UpdateAmbientBreathing()
    {
        if (!breathingEnabled) { ambientScaleFactor = 1f; return; }
        float f = Mathf.Max(0.01f, breathingFrequency);
        float s = Mathf.Sin(Time.time * Mathf.PI * 2f * f);
        ambientScaleFactor = 1f + s * Mathf.Max(0f, breathingAmplitude);
    }

    // —— 偶发摇摆
    void MaybeStartSway()
    {
        if (swayRoutine != null) return;
        if (Random.value < swayChancePerSecond * Time.deltaTime)
            swayRoutine = StartCoroutine(SwayOnce());
    }

    IEnumerator SwayOnce()
    {
        float dur = Random.Range(swayDurationRange.x, swayDurationRange.y);
        float half = dur * 0.5f;
        float w = Mathf.PI * 2f * Mathf.Max(0.01f, swayFrequency);
        float maxA = Mathf.Max(0f, swayAngleDegrees);

        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float env = t < half ? Mathf.SmoothStep(0f, 1f, t / half)
                                 : Mathf.SmoothStep(1f, 0f, (t - half) / half);
            float yaw  = Mathf.Sin(Time.time * w) * maxA * env;
            float roll = Mathf.Sin(Time.time * w * 0.5f + 0.7f) * maxA * 0.5f * env;
            ambientSwayRotOffset = Quaternion.Euler(0f, yaw, roll);
            yield return null;
        }

        ambientSwayRotOffset = Quaternion.identity;
        swayRoutine = null;
    }

    // —— 缩放钳位：围绕给定基线
    Vector3 ClampScaleAroundBaseline(Vector3 baseline, Vector3 candidate)
    {
        Vector3 minV = baseline * minScaleMultiplier;
        Vector3 maxV = baseline * maxScaleMultiplier;
        return new Vector3(
            Mathf.Clamp(candidate.x, minV.x, maxV.x),
            Mathf.Clamp(candidate.y, minV.y, maxV.y),
            Mathf.Clamp(candidate.z, minV.z, maxV.z)
        );
    }

    static float EaseOutCubic(float x) { return 1f - Mathf.Pow(1f - x, 3f); }
    static float EaseInCubic(float x)  { return x * x * x; }

    // —— 调试
    void OnGUI()
    {
        if (!showDebugInfo) return;

        GUILayout.BeginArea(new Rect(10, 10, 520, 340), GUI.skin.box);
        GUILayout.Label("=== 手距触发 脉冲+抖动 · 舒缓随机版（相对当前旋转）===");
        GUILayout.Label($"左手: {(hasLeftHandData ? "✓" : "✗")}  右手: {(hasRightHandData ? "✓" : "✗")}");
        GUILayout.Label($"阈值{distanceChangeThreshold:F4} 平滑{distanceSmoothing:F2} 间隔{retriggerDelay:F2}s");
        GUILayout.Label($"脉冲 放大{pulseScaleAmount:P0} 时长{pulseDuration:F2}s 上升{pulseUpPortion:F2}");
        GUILayout.Label($"抖动 频率{shakeFrequency:F1}Hz 位移{shakePositionAmplitude:F3} 旋转{shakeRotationDegrees:F1}°");
        GUILayout.Label($"呼吸 幅度{breathingAmplitude:P1} 频率{breathingFrequency:F2}Hz");
        GUILayout.Label($"漂浮 幅度{driftPositionAmplitude:F3} 频率{driftFrequency:F2}Hz");
        GUILayout.Label($"摇摆 概率/秒{swayChancePerSecond:F2} 振幅±{swayAngleDegrees:F1}° 频率{swayFrequency:F2}Hz");
        GUILayout.Label($"深呼吸 几率{deepPulseChance:P0} 规模×{deepPulseScaleMultiplier:F1} 时长×{deepPulseDurationMultiplier:F1}");
        GUILayout.EndArea();
    }
}
