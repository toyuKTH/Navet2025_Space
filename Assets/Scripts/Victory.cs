using System;
using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Rect = UnityEngine.Rect;

public class PeaceSignTreeTriggerForSpawner : MonoBehaviour
{
    [Header("Holistic（不填会自动找名为 Solution 的对象）")]
    public HolisticTrackingSolution holistic;

    [Header("要驱动的生成器（TreeSpawnerOnSphere）")]
    public TreeSpawnerOnSphere spawner;

    [Header("触发与时序")]
    [Tooltip("✌️ 成功后保持生长的时长（秒），到点自动执行 BeginDisappear()")]
    public float burstDuration = 1.2f;
    [Tooltip("两次完整爆发之间的冷却（秒）")]
    public float retriggerDelay = 1.2f;
    [Tooltip("同一次爆发里，若还在 Growing/Disappearing，不接受新的触发")]
    public bool lockDuringPhase = true;

    [Header("手势判定与不冲突门控")]
    [Tooltip("✌️需要保持的最短时间，避免抖动误触")]
    public float holdTime = 0.20f;
    [Tooltip("必须保持“只有单手在画面里”至少这么久，才允许识别✌️")]
    public float soloHandMinTime = 0.25f;

    [Header("调试")]
    public bool debugGUI = true;
    public bool verboseLogs = true;

    // —— MediaPipe Hands 索引 —— //
    const int WRIST = 0;
    const int THUMB_CMC = 1, THUMB_MCP = 2, THUMB_IP = 3, THUMB_TIP = 4;
    const int INDEX_MCP = 5, INDEX_PIP = 6, INDEX_DIP = 7, INDEX_TIP = 8;
    const int MIDDLE_MCP = 9, MIDDLE_PIP = 10, MIDDLE_DIP = 11, MIDDLE_TIP = 12;
    const int RING_MCP = 13, RING_PIP = 14, RING_DIP = 15, RING_TIP = 16;
    const int PINKY_MCP = 17, PINKY_PIP = 18, PINKY_DIP = 19, PINKY_TIP = 20;

    // —— 回调线程写 / 主线程读 —— //
    private readonly Vector3[] leftLms  = new Vector3[21];
    private readonly Vector3[] rightLms = new Vector3[21];
    private readonly object leftLock  = new object();
    private readonly object rightLock = new object();
    private volatile bool leftValid  = false;
    private volatile bool rightValid = false;
    private volatile bool leftNew    = false;
    private volatile bool rightNew   = false;

    // —— 门控计时 —— //
    private float leftHold = 0f, rightHold = 0f;
    private float soloTimer = 0f;
    private float lastTriggerAt = -999f;
    private bool inBurst = false;            // 一次爆发进行中（用来屏蔽重复）

    void Log(string s) { if (verboseLogs) Debug.Log("[Peace→Spawner] " + s); }

    void Start()
    {
        StartCoroutine(ConnectHolistic());

        if (!spawner)
            Debug.LogWarning("[Peace→Spawner] 请在 Inspector 指定 TreeSpawnerOnSphere");
        else
            spawner.OnAllCleared += OnSpawnerAllCleared;
    }

    IEnumerator ConnectHolistic()
    {
        yield return new WaitForSeconds(1.0f);
        if (!holistic)
        {
            var go = GameObject.Find("Solution");
            if (go) holistic = go.GetComponent<HolisticTrackingSolution>();
            Log(go ? "已找到 Solution 并获取 Holistic" : "未找到 Solution（可手动拖到 Inspector）");
        }
        if (!holistic) yield break;

        TryRegisterCallbacks(holistic);
    }

    void TryRegisterCallbacks(HolisticTrackingSolution h)
    {
        try
        {
            var t = h.GetType();
            HolisticTrackingGraph g = null;

            var f = t.GetField("graphRunner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f != null) g = f.GetValue(h) as HolisticTrackingGraph;
            if (g == null)
            {
                var p = t.GetProperty("graphRunner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (p != null) g = p.GetValue(h) as HolisticTrackingGraph;
            }
            if (g == null) { Log("❌ 拿不到 graphRunner"); return; }

            g.OnLeftHandLandmarksOutput  += OnLeft;
            g.OnRightHandLandmarksOutput += OnRight;
            Log("✅ 已注册手部 landmarks 回调");
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    // —— 回调：只拷贝数据 —— //
    void OnLeft(object s, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var list = e.packet?.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { leftValid = false; return; }
        lock (leftLock)
        {
            for (int i = 0; i < 21; i++) { var lm = list.Landmark[i]; leftLms[i] = new Vector3(lm.X, lm.Y, lm.Z); }
            leftValid = true; leftNew = true;
        }
    }
    void OnRight(object s, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var list = e.packet?.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { rightValid = false; return; }
        lock (rightLock)
        {
            for (int i = 0; i < 21; i++) { var lm = list.Landmark[i]; rightLms[i] = new Vector3(lm.X, lm.Y, lm.Z); }
            rightValid = true; rightNew = true;
        }
    }

    void Update()
    {
        // —— 单手门控：只有“仅单手”持续 >= soloHandMinTime 才允许识别 ✌️ —— //
        bool singleLeft  = leftValid  && !rightValid;
        bool singleRight = rightValid && !leftValid;
        bool soloNow     = singleLeft || singleRight;
        soloTimer = soloNow ? soloTimer + Time.deltaTime : 0f;

        if (soloTimer < soloHandMinTime) { leftHold = rightHold = 0f; return; }

        // —— 判定✌️（仅对当前存在的那只手） —— //
        bool leftPeace = false, rightPeace = false;
        if (singleLeft)  { Vector3[] l; lock (leftLock)  l = (Vector3[])leftLms.Clone();  leftPeace  = IsPeace(l); }
        if (singleRight) { Vector3[] r; lock (rightLock) r = (Vector3[])rightLms.Clone(); rightPeace = IsPeace(r); }

        leftHold  = singleLeft  && leftPeace  ? leftHold  + Time.deltaTime : 0f;
        rightHold = singleRight && rightPeace ? rightHold + Time.deltaTime : 0f;

        bool ok = (leftHold >= holdTime) || (rightHold >= holdTime);
        if (!ok) return;

        // —— Re-entrancy & 冷却 & 阶段锁 —— //
        if (Time.time - lastTriggerAt < retriggerDelay) return;
        if (inBurst && lockDuringPhase) return;
        if (spawner && lockDuringPhase && spawner.CurrentPhase != TreeSpawnerOnSphere.Phase.Idle) return;

        // 触发一次完整爆发
        lastTriggerAt = Time.time;
        StartCoroutine(DoBurst());
    }

    IEnumerator DoBurst()
    {
        if (!spawner) yield break;

        inBurst = true;
        Log("✌️ 触发 BeginGrow()");
        spawner.BeginGrow();

        // 等待一段时间后开始消失
        yield return new WaitForSeconds(Mathf.Max(0f, burstDuration));

        Log("✌️ 进入 BeginDisappear()");
        spawner.BeginDisappear();

        // 如果你希望这里“等到全部消失再解锁”，注释掉下一行，把解锁放到 OnSpawnerAllCleared 里
        // inBurst = false;  //（我们选择在 AllCleared 回调里解锁更稳妥）
    }

    void OnSpawnerAllCleared()
    {
        Log("🌲 全部清空 (OnAllCleared) —— 一次爆发结束");
        inBurst = false;
    }

    // ✌️判定：食指+中指伸直，其它弯曲，并且两指尖有一定间距（像“V”）
    bool IsPeace(Vector3[] lm)
    {
        if (lm == null || lm.Length < 21) return false;

        float BendAngle(Vector3 a, Vector3 b)
        {
            float na = a.magnitude, nb = b.magnitude;
            if (na < 1e-5f || nb < 1e-5f) return 180f;
            float cos = Mathf.Clamp(Vector3.Dot(a / na, b / nb), -1f, 1f);
            return Mathf.Acos(cos) * Mathf.Rad2Deg;
        }
        bool FingerExtended(int mcp, int pip, int tip, float angTh = 30f, float lenRatio = 1.2f)
        {
            Vector3 v1 = lm[pip] - lm[mcp], v2 = lm[tip] - lm[pip];
            float ang = BendAngle(v1, v2); ang = Mathf.Min(ang, 180f - ang);
            float Lmt = (lm[tip] - lm[mcp]).magnitude, Lmp = (lm[pip] - lm[mcp]).magnitude;
            return (ang < angTh) && (Lmt > Lmp * lenRatio);
        }
        bool FingerCurled(int mcp, int pip, int tip, float angTh = 45f)
        {
            Vector3 v1 = lm[pip] - lm[mcp], v2 = lm[tip] - lm[pip];
            float ang = Vector3.Angle(v1, v2);
            float Lmt = (lm[tip] - lm[mcp]).magnitude, Lmp = (lm[pip] - lm[mcp]).magnitude;
            return (ang > angTh) && (Lmt < Lmp * 1.05f);
        }

        bool indexUp  = FingerExtended(INDEX_MCP,  INDEX_PIP,  INDEX_TIP);
        bool middleUp = FingerExtended(MIDDLE_MCP, MIDDLE_PIP, MIDDLE_TIP);
        bool ringDown  = FingerCurled(RING_MCP,  RING_PIP,  RING_TIP);
        bool pinkyDown = FingerCurled(PINKY_MCP, PINKY_PIP, PINKY_TIP);
        bool thumbDown = FingerCurled(THUMB_CMC, THUMB_IP,  THUMB_TIP, 35f); // 拇指要求放宽

        bool ok = indexUp && middleUp && ringDown && pinkyDown && (thumbDown || true);

        // “V” 间距约束
        float vGap = Vector3.Distance(lm[INDEX_TIP], lm[MIDDLE_TIP]);
        float palm = Vector3.Distance(lm[WRIST], lm[MIDDLE_MCP]);
        if (palm > 1e-4f) ok = ok && (vGap > palm * 0.25f);

        return ok;
    }

    void OnGUI()
    {
        if (!debugGUI) return;
        GUILayout.BeginArea(new Rect(10, 10, 420, 140), GUI.skin.box);
        GUILayout.Label("PeaceSign → TreeSpawnerOnSphere");
        GUILayout.Label($"leftValid={leftValid}  rightValid={rightValid}   solo={soloTimer:0.00}/{soloHandMinTime:0.00}");
        GUILayout.Label($"hold L/R={leftHold:0.00}/{rightHold:0.00}   burst={inBurst}   phase={(spawner? spawner.CurrentPhase.ToString() : "N/A")}");
        GUILayout.Label($"cooldown={Mathf.Max(0f, retriggerDelay - (Time.time - lastTriggerAt)):0.00}s");
        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        if (spawner) spawner.OnAllCleared -= OnSpawnerAllCleared;
    }
}
