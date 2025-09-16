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

    [Header("要缩放的目标（为空则缩放自己）")]
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

    [Header("Storm（简单：超过阈值则开，带滞回）")]
    public RockStormSpawner storm;
    [Range(0f, 1f)] public float stormOnThreshold = 0.35f;   // t 超过此值打开
    [Range(0f, 1f)] public float stormOffThreshold = 0.30f;  // t 低于此值关闭（避免抖动）

    [Header("MIDI（最小：把 t 作为 timbre01 连续输出）")]
    public MIDIStageManager stageManager;
    public bool sendContinuousToMIDI = true;
    [Range(0f, 1f)] public float midiPitch01 = 0.5f;
    [Range(0f, 1f)] public float midiRate01  = 0.5f;

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

    // 计算状态
    private float filteredDist = -1f;   // 平滑后的距离
    private Vector3 baseScale;

    // 调试/状态
    private float nextSummaryAt = 0f;
    private string lastEarlyExit = "";
    private bool stormOn = false;

    // 手部关键点索引
    const int WRIST = 0;
    const int INDEX_MCP = 5;

    void Log(string s) { if (verboseLogs) Debug.Log("[Shaking] " + s); }

    void Start()
    {
        if (!target) target = transform;
        baseScale = target.localScale;

        StartCoroutine(Connect());
        StartCoroutine(Watchdog());
    }

    IEnumerator Connect()
    {
        // 等 Mediapipe 起
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

        if (!(leftValid && rightValid))
        {
            EarlyExit("没有同时拿到左右手有效数据");
            MaybeStormOff();
            return;
        }

        float dt = Mathf.Abs(leftTime - rightTime);
        if (dt > dataMaxAge)
        {
            EarlyExit($"左右手时间差过大 Δt={dt:0.000}s > {dataMaxAge:0.000}s");
            MaybeStormOff();
            return;
        }

        // —— 复制 landmark 到临时变量后计算 —— 
        Vector3 lp, rp;
        lock (leftLock)  lp = PalmPoint(leftLms);
        lock (rightLock) rp = PalmPoint(rightLms);

        // 用 xy 归一化坐标算欧氏距离
        float raw = Vector2.Distance(new Vector2(lp.x, lp.y), new Vector2(rp.x, rp.y));

        // 低通
        if (filteredDist < 0f) filteredDist = raw;
        else
        {
            float a = 1f - Mathf.Clamp01(distanceSmoothing);
            filteredDist = Mathf.Lerp(filteredDist, raw, a);
        }

        // 距离→0..1（按你的镜头需要调整这两个阈值）
        float t = Mathf.Clamp01(Mathf.InverseLerp(0.05f, 0.45f, filteredDist));

        // ===== 视觉：缩放 =====
        float s = Mathf.Lerp(scaleMin, scaleMax, t);
        target.localScale = baseScale * s;

        // ===== Storm：简单阈值 + 滞回 =====
        if (storm)
        {
            if (!stormOn && t >= stormOnThreshold) { storm.Activate(true); stormOn = true; }
            else if (stormOn && t <= stormOffThreshold) { storm.Activate(false); stormOn = false; }
        }

        // ===== MIDI：把 t 连续发给 timbre01（最简）=====
        if (stageManager && sendContinuousToMIDI)
        {
            stageManager.ApplyControls(midiPitch01, t, midiRate01);
        }

        // 汇总日志
        if (Time.time >= nextSummaryAt)
        {
            nextSummaryAt = Time.time + Mathf.Max(0.1f, summaryLogInterval);
            Log($"UPD: raw={raw:0.000} filtered={filteredDist:0.000} t={t:0.00} scale={s:0.00} Δt={dt:0.000}s storm={(stormOn?"ON":"OFF")}");
        }

        lastEarlyExit = "";
    }

    // ============== 工具/调试 ==============
    void MaybeStormOff()
    {
        if (storm && stormOn) { storm.Activate(false); stormOn = false; }
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
            $"L=({lp.x:0.000},{lp.y:0.000}) R=({rp.x:0.000},{rp.y:0.000}) filtered={filteredDist:0.000} storm={stormOn}");
    }

    void OnGUI()
    {
        if (!debugGUI) return;
        GUILayout.BeginArea(new Rect(10, 10, 520, 170), GUI.skin.box);
        GUILayout.Label("Shaking · 双手距离 → 缩放（含 Storm/MIDI）  (按 L 打快照)");
        GUILayout.Label($"leftValid={leftValid} tL={leftTime:0.00} | rightValid={rightValid} tR={rightTime:0.00}");
        GUILayout.Label($"Δt={Mathf.Abs(leftTime - rightTime):0.000}s  (max {dataMaxAge:0.000}s)");
        GUILayout.Label($"dist(filtered)={filteredDist:0.000}  smoothing={distanceSmoothing:0.00}");
        GUILayout.Label($"scale=[{scaleMin:0.00}..{scaleMax:0.00}]  storm={(stormOn ? "ON" : "OFF")}");
        GUILayout.EndArea();
    }
}
