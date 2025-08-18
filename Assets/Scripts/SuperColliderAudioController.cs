using UnityEngine;
using System.Net.Sockets;
using System.Text;
using System.Globalization;
using System.Collections;
using System.IO;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using Rect = UnityEngine.Rect;
using Screen = UnityEngine.Screen;

public class HandDistanceToSuperCollider : MonoBehaviour
{
    [Header("SuperCollider 连接")]
    public string supercolliderIP = "127.0.0.1";
    public int supercolliderPort = 57120;
    public string oscAddress = "/unity/hands/distance";

    [Header("检测/平滑")]
    [Tooltip("对手距做低通平滑，0~1 越大越平滑")]
    [Range(0f, 1f)] public float distanceSmoothing = 0.85f;
    [Tooltip("Holistic 归一化范围内映射到 0~1")]
    public float normMin = 0.10f;
    public float normMax = 0.80f;

    [Header("边缘触发(防抖)")]
    [Tooltip("两次触发之间的最短间隔（秒）")]
    public float minSendInterval = 0.12f;
    [Tooltip("与上次触发值相比，变化量达到此阈值才触发（0~1）")]
    [Range(0f, 1f)] public float changeThreshold = 0.06f;
    [Tooltip("迟滞带宽：进入阈值后，需越过此带宽才允许下一次反向触发")]
    [Range(0f, 1f)] public float hysteresisBand = 0.02f;

    [Header("稳定性")]
    [Tooltip("只有双手都被检测到时才发送")]
    public bool requireBothHands = true;
    [Tooltip("双手稳定帧数（避免闪断）")]
    public int requiredStableFrames = 2;

    [Header("调试")]
    public bool showDebugInfo = true;
    public bool showDetailedLog = false;

    // UDP
    private UdpClient udpClient;
    private bool isConnected = false;

    // 手部数据
    private Vector3[] leftHand = new Vector3[21];
    private Vector3[] rightHand = new Vector3[21];
    private bool hasLeft = false, hasRight = false;
    private bool landmarksUpdated = false;

    // 距离状态
    private float filteredDistance = 0f;
    private float prevFilteredDistance = 0f;
    private bool firstDistance = true;
    private int bothHandsStableCounter = 0;

    // 触发状态
    private float lastSentTime = -999f;
    private float lastSentValue = 0f;   // 上次发送的（0~1）值
    private float latchCenter = -1f;    // 迟滞中心（进入触发后锁存的参考值）

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
            // 初次不发送实际值，避免刚联通触发
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
        if (solution == null)
        {
            Debug.LogWarning("未找到名为 Solution 的对象（Holistic 根节点）");
            yield break;
        }

        var holistic = solution.GetComponent<HolisticTrackingSolution>();
        if (holistic == null)
        {
            Debug.LogError("Solution 上未找到 HolisticTrackingSolution 组件");
            yield break;
        }

        TryRegisterCallbacks(holistic);
        // 以 10Hz 分析
        InvokeRepeating(nameof(AnalyzeHands), 0.1f, 0.1f);
    }

    void TryRegisterCallbacks(HolisticTrackingSolution holistic)
    {
        try
        {
            var field = holistic.GetType().GetField("graphRunner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            HolisticTrackingGraph graph = null;

            if (field != null)
                graph = field.GetValue(holistic) as HolisticTrackingGraph;
            else
            {
                var prop = holistic.GetType().GetProperty("graphRunner",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (prop != null)
                    graph = prop.GetValue(holistic) as HolisticTrackingGraph;
            }

            if (graph == null)
            {
                Debug.LogWarning("未能通过反射获取 HolisticTrackingGraph");
                return;
            }

            graph.OnLeftHandLandmarksOutput += OnLeftHand;
            graph.OnRightHandLandmarksOutput += OnRightHand;
            Debug.Log("✓ 已注册左右手关键点回调");
        }
        catch (System.Exception e)
        {
            Debug.LogError("注册回调失败：" + e.Message);
        }
    }

    // 回调：左手
    void OnLeftHand(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var pkt = e.packet;
        if (pkt == null) { hasLeft = false; return; }
        var list = pkt.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { hasLeft = false; return; }

        for (int i = 0; i < 21; i++)
        {
            var lm = list.Landmark[i];
            leftHand[i] = new Vector3(lm.X, lm.Y, lm.Z);
        }
        hasLeft = true;
        landmarksUpdated = true;
    }

    // 回调：右手
    void OnRightHand(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var pkt = e.packet;
        if (pkt == null) { hasRight = false; return; }
        var list = pkt.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) { hasRight = false; return; }

        for (int i = 0; i < 21; i++)
        {
            var lm = list.Landmark[i];
            rightHand[i] = new Vector3(lm.X, lm.Y, lm.Z);
        }
        hasRight = true;
        landmarksUpdated = true;
    }

    void AnalyzeHands()
    {
        if (!landmarksUpdated) return;

        bool both = hasLeft && hasRight;
        if (both)
        {
            float raw = Vector3.Distance(leftHand[0], rightHand[0]);
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
            if (showDetailedLog) Debug.Log($"手距 raw:{raw:F4} filtered:{filteredDistance:F4}");
        }
        else
        {
            bothHandsStableCounter = 0;
            return;
        }

        if (requireBothHands && bothHandsStableCounter < requiredStableFrames) return;

        // 归一化到 0~1
        float n = Mathf.InverseLerp(normMin, normMax, filteredDistance);
        n = Mathf.Clamp01(n);

        TryEdgeTriggerSend(n);
        landmarksUpdated = false;
    }

    /// <summary>
    /// 边缘触发发送：变化超过阈值且跨过迟滞带，并满足最小间隔
    /// </summary>
    void TryEdgeTriggerSend(float value01)
    {
        float now = Time.time;
        if (now - lastSentTime < minSendInterval) return;

        // 与上次触发值比较的变化量
        float delta = Mathf.Abs(value01 - lastSentValue);

        // 首次触发：以当前值为基准，建立迟滞中心
        if (lastSentTime < -10f)
        {
            // 初始化（不发送，避免一上电就响）
            lastSentValue = value01;
            latchCenter = value01;
            lastSentTime = now;
            return;
        }

        // 未越过“变化阈值”则不发
        if (delta < changeThreshold)
            return;

        // 迟滞：只有越过“锁存中心±带宽”才允许再次触发
        if (latchCenter >= 0f)
        {
            float lower = latchCenter - hysteresisBand * 0.5f;
            float upper = latchCenter + hysteresisBand * 0.5f;
            if (value01 > lower && value01 < upper)
            {
                // 仍在迟滞带内，不触发
                return;
            }
        }

        // 通过所有条件 → 发送
        SendOSC(oscAddress, value01);

        // 更新状态
        lastSentTime = now;
        lastSentValue = value01;
        latchCenter = value01; // 以当前触发值为新的迟滞中心
    }

    // === 使用真正的 OSC 协议 ===
    void SendOSC(string address, float value)
    {
        if (!isConnected || udpClient == null) return;

        try
        {
            byte[] packet = BuildOSCMessage(address, value);
            udpClient.Send(packet, packet.Length);

            if (showDebugInfo)
                Debug.Log($"📤 OSC → {supercolliderIP}:{supercolliderPort} | {address} {value:F3}");
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
        GUILayout.BeginArea(new Rect(Screen.width - 340, 10, 330, 230), GUI.skin.box);
        GUILayout.Label("=== Hands → SuperCollider (边缘触发) ===");
        GUILayout.Label($"连接: {(isConnected ? "✓" : "✗")} {supercolliderIP}:{supercolliderPort}");
        GUILayout.Label($"地址: {oscAddress}");
        GUILayout.Label($"平滑: {distanceSmoothing:F2}  范围[{normMin:F2},{normMax:F2}]");
        GUILayout.Label($"最短间隔: {minSendInterval:F3}s  阈值: {changeThreshold:F3}  迟滞: {hysteresisBand:F3}");
        GUILayout.Label($"双手: 左{(hasLeft ? "✓" : "✗")} 右{(hasRight ? "✓" : "✗")} 稳定帧:{bothHandsStableCounter}");
        GUILayout.Label($"距(滤波): {filteredDistance:F4}");
        float n = Mathf.Clamp01(Mathf.InverseLerp(normMin, normMax, filteredDistance));
        GUILayout.Label($"距(0~1): {n:F3}");
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
