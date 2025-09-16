using System;
using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Rect = UnityEngine.Rect;   // 关键：消除 Mediapipe.Rect / UnityEngine.Rect 歧义

public class TwoHandScaleProbeSafe : MonoBehaviour
{
    [Header("Holistic 引用（不填会自动找名为 Solution 的对象）")]
    public HolisticTrackingSolution holistic;

    [Header("要缩放的目标（为空则缩放自己）")]
    public Transform target;

    [Header("缩放范围")]
    public float scaleMin = 0.6f;
    public float scaleMax = 1.4f;

    [Header("滤波/稳定")]
    [Range(0f, 0.99f)] public float distanceSmoothing = 0.85f;
    [Tooltip("左右手数据时间差最大容忍（秒），超过则认为不同步不计算")]
    public float dataMaxAge = 0.25f;

    [Header("掌心 vs 手腕")]
    public bool usePalmCenter = true;

    [Header("调试")]
    public bool debugGUI = true;
    public bool verboseLogs = true;            // 控制台详细日志
    public bool logEachCallback = false;       // 每 N 次回调打一条（避免刷屏）
    public int  logEveryNCallbacks = 10;
    public float summaryLogInterval = 1.0f;    // 汇总日志间隔（秒）
    public KeyCode snapshotKey = KeyCode.L;    // L 键打印快照

    // ===== Mediapipe landmark 缓存（回调线程写，主线程读） =====
    private readonly Vector3[] leftLms  = new Vector3[21];
    private readonly Vector3[] rightLms = new Vector3[21];
    private volatile bool leftValid  = false;
    private volatile bool rightValid = false;
    private volatile bool leftNew    = false;
    private volatile bool rightNew   = false;
    private readonly object leftLock  = new object();
    private readonly object rightLock = new object();

    // ===== 主线程状态 =====
    private float leftTime  = -999f;   // 在 Update()（主线程）里更新
    private float rightTime = -999f;
    private int leftCount  = 0;
    private int rightCount = 0;

    // ===== 计算状态 =====
    private float filteredDist = -1f;
    private Vector3 baseScale;
    private float nextSummaryAt = 0f;
    private string lastEarlyExit = "";

    // 常量（MediaPipe Hands 索引）
    const int WRIST = 0;
    const int INDEX_MCP = 5;

    void Log(string s)
    {
        if (verboseLogs) Debug.Log($"[TwoHandScaleProbeSafe] {s}");
    }

    void Start()
    {
        if (!target) target = transform;
        baseScale = target.localScale;

        Log("Start：初始化完成，准备连接 Holistic...");
        StartCoroutine(Connect());
        StartCoroutine(Watchdog());
    }

    IEnumerator Connect()
    {
        // 给 Mediapipe 一点初始化时间
        yield return new WaitForSeconds(1.2f);

        if (!holistic)
        {
            var go = GameObject.Find("Solution");
            Log(go ? "找到对象 'Solution'，尝试获取 HolisticTrackingSolution" : "未找到名为 'Solution' 的对象");
            if (go) holistic = go.GetComponent<HolisticTrackingSolution>();
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
            Log("TryRegister：反射获取 graphRunner...");
            var t = h.GetType();
            HolisticTrackingGraph g = null;

            var field = t.GetField("graphRunner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                g = field.GetValue(h) as HolisticTrackingGraph;
                Log("TryRegister：通过字段拿到 graphRunner");
            }

            if (g == null)
            {
                var prop = t.GetProperty("graphRunner",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop != null)
                {
                    g = prop.GetValue(h) as HolisticTrackingGraph;
                    Log("TryRegister：通过属性拿到 graphRunner");
                }
            }

            if (g == null)
            {
                Log("❌ TryRegister：拿不到 graphRunner（可能版本接口不同）");
                return;
            }

            g.OnLeftHandLandmarksOutput  += OnLeft;
            g.OnRightHandLandmarksOutput += OnRight;
            Log("✅ TryRegister：已注册左右手 landmark 回调");
        }
        catch (Exception e)
        {
            Debug.LogError("[TwoHandScaleProbeSafe] ❌ TryRegister 异常");
            Debug.LogException(e);
        }
    }

    // ============== 回调线程：只缓存数据，不用 Time.* ==============
    void OnLeft(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        try
        {
            var list = e.packet?.Get(NormalizedLandmarkList.Parser);
            if (list == null || list.Landmark.Count < 21)
            {
                leftValid = false;
                return;
            }

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

            if (logEachCallback && ((leftCount + 1) % logEveryNCallbacks == 0))
            {
                var p = PalmPointThreadSafe(leftLms, leftLock);
                Log($"OnLeft 回调：palm=({p.x:0.000},{p.y:0.000})");
            }
        }
        catch { leftValid = false; }
    }

    void OnRight(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        try
        {
            var list = e.packet?.Get(NormalizedLandmarkList.Parser);
            if (list == null || list.Landmark.Count < 21)
            {
                rightValid = false;
                return;
            }

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

            if (logEachCallback && ((rightCount + 1) % logEveryNCallbacks == 0))
            {
                var p = PalmPointThreadSafe(rightLms, rightLock);
                Log($"OnRight 回调：palm=({p.x:0.000},{p.y:0.000})");
            }
        }
        catch { rightValid = false; }
    }

    // ============== 主线程 Update：打时间戳 + 计算距离/缩放 ==============
    void Update()
    {
        if (Input.GetKeyDown(snapshotKey))
            DumpSnapshot("手动快照");

        // 把“新数据”标记转为主线程时间戳
        if (leftNew)  { leftTime  = Time.time; leftNew  = false; leftCount++;  }
        if (rightNew) { rightTime = Time.time; rightNew = false; rightCount++; }

        // 逐步早退诊断
        if (!(leftValid && rightValid))
        {
            EarlyExit("没有同时拿到左右手有效数据");
            return;
        }

        float dt = Mathf.Abs(leftTime - rightTime);
        if (dt > dataMaxAge)
        {
            EarlyExit($"左右手时间差过大 Δt={dt:0.000}s > {dataMaxAge:0.000}s");
            return;
        }

        // 复制到本地临时向量再算（尽量减少锁住时间）
        Vector3 lp, rp;
        lock (leftLock)  lp = PalmPoint(leftLms);
        lock (rightLock) rp = PalmPoint(rightLms);

        float raw = Vector2.Distance(new Vector2(lp.x, lp.y), new Vector2(rp.x, rp.y));

        // 低通滤波
        if (filteredDist < 0f) filteredDist = raw;
        else
        {
            float a = 1f - Mathf.Clamp01(distanceSmoothing);
            filteredDist = Mathf.Lerp(filteredDist, raw, a);
        }

        // 距离→0..1（按你的镜头需要调整这两个阈值）
        float t = Mathf.Clamp01(Mathf.InverseLerp(0.05f, 0.45f, filteredDist));

        float s = Mathf.Lerp(scaleMin, scaleMax, t);
        target.localScale = baseScale * s;

        // 汇总日志
        if (Time.time >= nextSummaryAt)
        {
            nextSummaryAt = Time.time + Mathf.Max(0.1f, summaryLogInterval);
            Log($"UPD：rawDist={raw:0.000} filtered={filteredDist:0.000} t={t:0.00} scale={s:0.00} " +
                $"Δt={dt:0.000}s leftCnt={leftCount} rightCnt={rightCount}");
        }

        lastEarlyExit = ""; // 本帧成功
    }

    // ============== 小工具/调试 ==============
    void EarlyExit(string reason)
    {
        if (reason != lastEarlyExit)
            Log($"早退：{reason} (leftValid={leftValid}, rightValid={rightValid})");
        lastEarlyExit = reason;
    }

    Vector3 PalmPoint(Vector3[] lms)
    {
        if (!usePalmCenter) return lms[WRIST];
        return (lms[WRIST] + lms[INDEX_MCP]) * 0.5f;
    }

    Vector3 PalmPointThreadSafe(Vector3[] lms, object locker)
    {
        lock (locker)
        {
            if (!usePalmCenter) return lms[WRIST];
            return (lms[WRIST] + lms[INDEX_MCP]) * 0.5f;
        }
    }

    IEnumerator Watchdog()
    {
        float lastAny = Time.time;
        while (true)
        {
            if (leftValid || rightValid) lastAny = Time.time;

            if (Time.time - lastAny > 2f)
            {
                Log("⚠️ Watchdog：2s 内没有手数据 —— 请检查摄像头/光照/画面内是否有双手");
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
        Log($"[{tag}] leftValid={leftValid} tL={leftTime:0.000}  rightValid={rightValid} tR={rightTime:0.000}  Δt={dt:0.000} " +
            $"L=({lp.x:0.000},{lp.y:0.000}) R=({rp.x:0.000},{rp.y:0.000}) filtered={filteredDist:0.000}");
    }

    void OnGUI()
    {
        if (!debugGUI) return;
        GUILayout.BeginArea(new Rect(10, 10, 540, 180), GUI.skin.box);
        GUILayout.Label("TwoHandScaleProbeSafe · 双手距离 → 缩放  (按 L 打快照)");
        GUILayout.Label($"leftValid={leftValid} tL={leftTime:0.00}  |  rightValid={rightValid} tR={rightTime:0.00}");
        GUILayout.Label($"Δt={Mathf.Abs(leftTime - rightTime):0.000}s  (max {dataMaxAge:0.000}s)");
        GUILayout.Label($"dist(filtered)={filteredDist:0.000}  smoothing={distanceSmoothing:0.00}");
        GUILayout.Label($"scaleRange=[{scaleMin:0.00}..{scaleMax:0.00}]  target='{(target?target.name:"(self)")}'");
        if (!string.IsNullOrEmpty(lastEarlyExit))
            GUILayout.Label($"EarlyExit: {lastEarlyExit}");
        GUILayout.EndArea();
    }
}
