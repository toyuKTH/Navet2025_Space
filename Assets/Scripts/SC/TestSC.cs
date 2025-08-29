using UnityEngine;
using System.Net.Sockets;
using System.Text;
using System.Collections;
using System.IO;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Rect = UnityEngine.Rect;
using Screen = UnityEngine.Screen;

public class HandGesturesToSuperCollider : MonoBehaviour
{
    [Header("SuperCollider 连接")]
    public string supercolliderIP = "127.0.0.1";
    public int supercolliderPort = 57120;
    public string oscAddrDistance = "/unity/hands/distance";
    public string oscAddrThumbUp  = "/unity/gesture/thumbup";

    [Header("距离检测/平滑")]
    [Range(0f, 1f)] public float distanceSmoothing = 0.85f;
    public float normMin = 0.10f;
    public float normMax = 0.80f;
    public float minSendInterval = 0.12f;
    [Range(0f, 1f)] public float changeThreshold = 0.06f;
    [Range(0f, 1f)] public float hysteresisBand = 0.02f;
    public bool requireBothHands = true;
    public int requiredStableFrames = 2;

    
    public enum ThumbHand { Either, LeftOnly, RightOnly }
    [Header("Thumb Up 识别")]
    public ThumbHand thumbHand = ThumbHand.Either;
    [Tooltip("两次 ThumbUp 触发之间的最短间隔（秒）")]
    public float thumbMinInterval = 0.25f;
    [Tooltip("判断“其它手指是否收拢”的阈值（越小越严格）")]
    public float foldedRatioThreshold = 0.75f;
    [Tooltip("判断拇指是否竖起的角度阈值（与竖直方向的夹角，度）")]
    [Range(0f, 90f)] public float thumbUpAngleDeg = 35f;

    [Header("调试")]
    public bool showDebugInfo = true;
    public bool showDetailedLog = false;

    // UDP
    private UdpClient udpClient;
    private bool isConnected = false;

    // 手部数据
    private Vector3[] left = new Vector3[21];
    private Vector3[] right = new Vector3[21];
    private bool hasLeft = false, hasRight = false;
    private bool landmarksUpdated = false;

    // 距离状态
    private float filteredDistance = 0f;
    private float prevFilteredDistance = 0f;
    private bool firstDistance = true;
    private int bothHandsStableCounter = 0;

    private float lastSentTime = -999f;
    private float lastSentValue = 0f;   // 上次发送的0~1值
    private float latchCenter = -1f;

    // ThumbUp 防抖
    private float lastThumbTime = -999f;

    void Start()
    {
        InitializeUDP();
        StartCoroutine(ConnectToHolistic());
    }

    void InitializeUDP()
    {
        try
        {
            udpClient = new UdpClient();
            udpClient.Connect(supercolliderIP, supercolliderPort);
            isConnected = true;
        }
        catch (System.Exception e)
        {
            Debug.LogError("连接 SuperCollider 失败：" + e.Message);
            isConnected = false;
        }
    }

    IEnumerator ConnectToHolistic()
    {
        yield return new WaitForSeconds(1.5f);
        var solution = GameObject.Find("Solution");
        if (solution == null) { Debug.LogWarning("未找到名为 Solution 的对象"); yield break; }
        var holistic = solution.GetComponent<HolisticTrackingSolution>();
        if (holistic == null) { Debug.LogError("Solution 上未找到 HolisticTrackingSolution"); yield break; }

        TryRegisterCallbacks(holistic);
        // 以 10Hz 分析（足够实时且省资源）
        InvokeRepeating(nameof(Analyze), 0.1f, 0.1f);
    }

    void TryRegisterCallbacks(HolisticTrackingSolution holistic)
    {
        try
        {
            var field = holistic.GetType().GetField("graphRunner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            HolisticTrackingGraph graph = null;

            if (field != null) graph = field.GetValue(holistic) as HolisticTrackingGraph;
            if (graph == null)
            {
                var prop = holistic.GetType().GetProperty("graphRunner",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (prop != null) graph = prop.GetValue(holistic) as HolisticTrackingGraph;
            }

            if (graph == null) { Debug.LogWarning("未能通过反射获取 HolisticTrackingGraph"); return; }

            graph.OnLeftHandLandmarksOutput  += OnLeftHand;
            graph.OnRightHandLandmarksOutput += OnRightHand;
            Debug.Log("✓ 已注册左右手关键点回调");
        }
        catch (System.Exception e)
        {
            Debug.LogError("注册回调失败：" + e.Message);
        }
    }

    void OnLeftHand(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var pkt = e.packet;
        if (pkt == null) { hasLeft = false; return; }
        var list = pkt.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { hasLeft = false; return; }
        for (int i = 0; i < 21; i++) { var lm = list.Landmark[i]; left[i] = new Vector3(lm.X, lm.Y, lm.Z); }
        hasLeft = true; landmarksUpdated = true;
    }

    void OnRightHand(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var pkt = e.packet;
        if (pkt == null) { hasRight = false; return; }
        var list = pkt.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { hasRight = false; return; }
        for (int i = 0; i < 21; i++) { var lm = list.Landmark[i]; right[i] = new Vector3(lm.X, lm.Y, lm.Z); }
        hasRight = true; landmarksUpdated = true;
    }

    void Analyze()
    {
        if (!landmarksUpdated) return;

        // 1) 手距 → OSC(0~1)
        AnalyzeAndSendDistance();

        // 2) ThumbUp → OSC(bang)
        AnalyzeThumbUp();

        landmarksUpdated = false;
    }

    void AnalyzeAndSendDistance()
    {
        bool both = hasLeft && hasRight;
        if (both)
        {
            float raw = Vector3.Distance(left[0], right[0]); // wrist-wrist
            if (firstDistance)
            {
                filteredDistance = raw;
                prevFilteredDistance = raw;
                firstDistance = false;
                return; // 第一帧不触发
            }
            else
            {
                filteredDistance = Mathf.Lerp(prevFilteredDistance, raw, 1f - Mathf.Clamp01(distanceSmoothing));
                prevFilteredDistance = filteredDistance;
            }
            bothHandsStableCounter = Mathf.Min(bothHandsStableCounter + 1, 1000);
            if (showDetailedLog) Debug.Log($"dist raw:{raw:F4} filtered:{filteredDistance:F4}");
        }
        else
        {
            bothHandsStableCounter = 0;
            return;
        }

        if (requireBothHands && bothHandsStableCounter < requiredStableFrames) return;

        float n = Mathf.InverseLerp(normMin, normMax, filteredDistance);
        n = Mathf.Clamp01(n);
        TryEdgeTriggerSend(oscAddrDistance, n);
    }

    void AnalyzeThumbUp()
    {
        bool leftOK  = hasLeft  && IsThumbUp(left);
        bool rightOK = hasRight && IsThumbUp(right);

        bool trigger = false;
        switch (thumbHand)
        {
            case ThumbHand.LeftOnly:  trigger = leftOK;  break;
            case ThumbHand.RightOnly: trigger = rightOK; break;
            default:                  trigger = leftOK || rightOK; break;
        }

        if (!trigger) return;

        float now = Time.time;
        if (now - lastThumbTime < thumbMinInterval) return;
        lastThumbTime = now;

        // 作为 bang 发送 1.0
        SendOSC(oscAddrThumbUp, 1.0f);
        if (showDebugInfo) Debug.Log("👍 ThumbUp detected → OSC sent");
    }

    // === ThumbUp 启发式判定 ===
    // 1) 其它手指（食/中/环/小）的 TIP→PIP 长度相对 TIP→WRIST 的比值偏小（表示“收拢”）
    // 2) 拇指大致沿上方向（与“竖直向上”夹角小于阈值）
    bool IsThumbUp(Vector3[] h)
    {
        int WRIST = 0;
        int TH_TIP = 4, TH_IP = 3, TH_MCP = 2;
        int IX_TIP = 8, IX_PIP = 6;
        int MD_TIP = 12, MD_PIP = 10;
        int RG_TIP = 16, RG_PIP = 14;
        int LT_TIP = 20, LT_PIP = 18;

        // 其它手指是否“收拢”
        bool fingersFolded =
            IsFolded(h, IX_TIP, IX_PIP, WRIST) &&
            IsFolded(h, MD_TIP, MD_PIP, WRIST) &&
            IsFolded(h, RG_TIP, RG_PIP, WRIST) &&
            IsFolded(h, LT_TIP, LT_PIP, WRIST);

        if (!fingersFolded) return false;

        // 拇指方向：TIP - MCP 与“上方向”(0,-1,0)夹角
        Vector2 v = new Vector2(h[TH_TIP].x - h[TH_MCP].x, h[TH_TIP].y - h[TH_MCP].y);
        if (v.sqrMagnitude < 1e-6f) return false;

        v.Normalize();
        // 注意 MediaPipe 坐标 y 轴向下，因此“上方向”为 (0,-1)
        Vector2 up = new Vector2(0f, -1f);
        float dot = Vector2.Dot(v, up);
        dot = Mathf.Clamp(dot, -1f, 1f);
        float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;

        return angle <= thumbUpAngleDeg;
    }

    bool IsFolded(Vector3[] h, int tip, int pip, int wrist)
    {
        float tp = Vector3.Distance(h[tip], h[pip]);
        float tw = Vector3.Distance(h[tip], h[wrist]);
        if (tw <= 1e-6f) return true;
        float ratio = tp / tw; // 越小越靠近手掌
        return ratio <= foldedRatioThreshold;
    }

    // === 边缘触发发送（距离） ===
    void TryEdgeTriggerSend(string addr, float value01)
    {
        float now = Time.time;
        if (now - lastSentTime < minSendInterval) return;

        float delta = Mathf.Abs(value01 - lastSentValue);

        if (lastSentTime < -10f)
        {
            lastSentValue = value01;
            latchCenter = value01;
            lastSentTime = now;
            return;
        }
        if (delta < changeThreshold) return;

        if (latchCenter >= 0f)
        {
            float lower = latchCenter - hysteresisBand * 0.5f;
            float upper = latchCenter + hysteresisBand * 0.5f;
            if (value01 > lower && value01 < upper) return;
        }

        SendOSC(addr, value01);
        lastSentTime = now;
        lastSentValue = value01;
        latchCenter = value01;
    }

    void SendOSC(string address, float value)
    {
        if (!isConnected || udpClient == null) return;

        try
        {
            byte[] packet = BuildOSCMessage(address, value);
            udpClient.Send(packet, packet.Length);
            if (showDebugInfo) Debug.Log($"📤 OSC {address} {value:F3}");
        }
        catch (System.Exception e)
        {
            Debug.LogError("发送 OSC 失败：" + e.Message);
        }
    }

    static byte[] BuildOSCMessage(string address, float value)
    {
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            void WritePaddedString(string s)
            {
                var bytes = Encoding.ASCII.GetBytes(s);
                bw.Write(bytes);
                bw.Write((byte)0);
                int pad = (4 - ((bytes.Length + 1) % 4)) % 4;
                for (int i = 0; i < pad; i++) bw.Write((byte)0);
            }

            WritePaddedString(address);
            WritePaddedString(",f");

            var fb = System.BitConverter.GetBytes(value);
            if (System.BitConverter.IsLittleEndian) System.Array.Reverse(fb);
            bw.Write(fb);
            bw.Flush();
            return ms.ToArray();
        }
    }

    void OnGUI()
    {
        if (!showDebugInfo) return;
        GUILayout.BeginArea(new Rect(Screen.width - 350, 10, 340, 260), GUI.skin.box);
        GUILayout.Label("=== Hands → SuperCollider ===");
        GUILayout.Label($"连接: {(isConnected ? "✓" : "✗")} {supercolliderIP}:{supercolliderPort}");
        GUILayout.Label($"距地址: {oscAddrDistance}  Thumb地址: {oscAddrThumbUp}");
        GUILayout.Label($"平滑:{distanceSmoothing:F2} 范围[{normMin:F2},{normMax:F2}]");
        GUILayout.Label($"间隔:{minSendInterval:F2}s 阈:{changeThreshold:F2} 迟滞:{hysteresisBand:F2}");
        GUILayout.Label($"双手: 左{(hasLeft ? "✓" : "✗")} 右{(hasRight ? "✓" : "✗")} 稳定帧:{bothHandsStableCounter}");
        GUILayout.Label($"距(滤波): {filteredDistance:F4} → 0~1:{Mathf.Clamp01(Mathf.InverseLerp(normMin,normMax,filteredDistance)):F3}");
        GUILayout.Label($"Thumb手: {thumbHand}  最短间隔:{thumbMinInterval:F2}s 角阈:{thumbUpAngleDeg:F0}°");
        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }
    }
}
