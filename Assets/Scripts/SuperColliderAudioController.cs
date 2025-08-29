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

public class HandDistanceToSuperCollider : MonoBehaviour
{
    // ========= 多目标：每个指向一个 SonificationMelody / 虚拟MIDI =========
    [System.Serializable]
    public class MidiTarget
    {
        [Header("目标与开关")]
        public string label = "Target A";
        public SonificationMelody melody;
        public bool enabled = true;

        [Header("连续控制（跟随手势 0..1）")]
        public bool controlPitchBend = true;
        [Tooltip("Pitch Bend 强度缩放：1=全幅(-1..+1)，0.5=半幅")]
        [Range(0f, 2f)] public float pitchBendScale = 1f;  // 乘到 (-1..+1) 上

        public bool controlRate = true;
        [Tooltip("Rate 映射范围（0=最慢，1=最快）")]
        public Vector2 rateRange01 = new Vector2(0.2f, 0.9f); // 把手势 n 映射到这个范围内的 rate01

        public bool controlTimbre = false;
        [Tooltip("Timbre 跟随强度，1=等幅，<1=减弱，>1=增强")]
        [Range(0f, 2f)] public float timbreScale = 1f;

        [Header("随机触发（发生在边缘触发时）")]
        public bool randomPulseOnEdge = true;
        [Tooltip("每次边缘触发时，以该概率执行一次脉冲")]
        [Range(0f, 1f)] public float pulseProbability = 0.35f;
        [Tooltip("脉冲期间将 Rate 拉到的值（0..1）")]
        [Range(0f, 1f)] public float pulseRate01 = 0.95f;
        [Tooltip("脉冲持续时间（秒）")]
        public float pulseDuration = 0.15f;
        [Tooltip("两次脉冲最小冷却（秒）")]
        public float pulseCooldown = 0.20f;

        [HideInInspector] public float _lastPulseTime = -999f;
        [HideInInspector] public float _lastAppliedRate01 = 0.5f; // 记录最近一次映射后的rate，给脉冲结束恢复
    }

    [Header("多目标列表（每个绑定一个不同的 SonificationMelody / 虚拟MIDI 端口）")]
    public MidiTarget[] targets;

    [Header("SuperCollider 连接（可选：仍保留 OSC 输出）")]
    public string supercolliderIP = "127.0.0.1";
    public int supercolliderPort = 57120;
    public string oscAddress = "/unity/hands/distance";

    [Header("检测/平滑")]
    [Range(0f, 1f)] public float distanceSmoothing = 0.85f;
    [Tooltip("把手势距离归一化到 0..1 的窗口")]
    public float normMin = 0.10f;
    public float normMax = 0.80f;

    [Header("边缘触发(防抖)")]
    public float minSendInterval = 0.12f;
    [Range(0f, 1f)] public float changeThreshold = 0.06f;
    [Range(0f, 1f)] public float hysteresisBand = 0.02f;

    [Header("稳定性")]
    public bool requireBothHands = true;
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
        InvokeRepeating(nameof(AnalyzeHands), 0.1f, 0.1f); // 10Hz
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
            float raw = Vector3.Distance(leftHand[0], rightHand[0]); // wrist-wrist
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
        float n = Mathf.Clamp01(Mathf.InverseLerp(normMin, normMax, filteredDistance));

        // —— 连续映射给每个目标 —— //
        ApplyContinuousToTargets(n);

        // —— OSC（可选） —— //
        TryEdgeTriggerSend(n);

        landmarksUpdated = false;
    }

    // 将连续手势值 n(0..1) 应用于每个目标的 Pitch/Rate/Timbre
    void ApplyContinuousToTargets(float n01)
    {
        if (targets == null) return;

        for (int i = 0; i < targets.Length; i++)
        {
            var t = targets[i];
            if (t == null || !t.enabled || t.melody == null) continue;

            // Pitch Bend: 0..1 -> -1..+1, 再乘缩放
            if (t.controlPitchBend)
            {
                float bend = Mathf.Lerp(-1f, 1f, n01) * Mathf.Clamp(t.pitchBendScale, 0f, 2f);
                bend = Mathf.Clamp(bend, -1f, 1f);
                t.melody.SetPitchBend01(bend);
            }

            // Rate: n01 映射到各自范围
            if (t.controlRate)
            {
                float mapped = Mathf.Lerp(
                    Mathf.Clamp01(t.rateRange01.x),
                    Mathf.Clamp01(t.rateRange01.y),
                    n01
                );
                t._lastAppliedRate01 = mapped; // 记录以便脉冲结束后恢复
                t.melody.SetRate01(mapped);
            }

            // Timbre: 放大/缩小
            if (t.controlTimbre)
            {
                float timbre = Mathf.Clamp01(n01 * Mathf.Max(0f, t.timbreScale));
                t.melody.SetTimbre01(timbre);
            }
        }
    }

    /// <summary>
    /// 边缘触发：用于“随机触发脉冲”（每目标独立概率/冷却/时长）
    /// </summary>
    void TryEdgeTriggerSend(float value01)
    {
        float now = Time.time;
        if (now - lastSentTime < minSendInterval) return;

        // 与上次触发值比较的变化量
        float delta = Mathf.Abs(value01 - lastSentValue);

        // 首次触发：建立迟滞中心，不发送
        if (lastSentTime < -10f)
        {
            lastSentValue = value01;
            latchCenter = value01;
            lastSentTime = now;
            return;
        }

        // 未越过变化阈值则不发
        if (delta < changeThreshold) return;

        // 迟滞带
        if (latchCenter >= 0f)
        {
            float lower = latchCenter - hysteresisBand * 0.5f;
            float upper = latchCenter + hysteresisBand * 0.5f;
            if (value01 > lower && value01 < upper) return;
        }

        // 通过所有条件 → 发送一次 OSC（可选）
        SendOSC(oscAddress, value01);

        // —— 同步对各目标做“随机脉冲” —— //
        MaybePulseTargets(value01, now);

        // 更新状态
        lastSentTime = now;
        lastSentValue = value01;
        latchCenter = value01;
    }

    // 对每个目标：按概率触发一次“速率脉冲”，使它短时间很快，然后恢复到连续映射值
    void MaybePulseTargets(float value01, float now)
    {
        if (targets == null) return;

        for (int i = 0; i < targets.Length; i++)
        {
            var t = targets[i];
            if (t == null || !t.enabled || t.melody == null) continue;
            if (!t.randomPulseOnEdge || !t.controlRate) continue; // 脉冲基于rate

            if (now - t._lastPulseTime < t.pulseCooldown) continue;
            if (Random.value > Mathf.Clamp01(t.pulseProbability)) continue;

            // 触发脉冲：把 rate 拉到指定值，持续一段时间，再恢复
            StartCoroutine(PulseRateCoroutine(t));
            t._lastPulseTime = now;
        }
    }

    IEnumerator PulseRateCoroutine(MidiTarget t)
    {
        if (t.melody == null) yield break;

        // 保存当前 Rate（已在 ApplyContinuousToTargets 记录进 _lastAppliedRate01）
        float before = Mathf.Clamp01(t._lastAppliedRate01);

        // 设置到脉冲 Rate
        float pulse = Mathf.Clamp01(t.pulseRate01);
        t.melody.SetRate01(pulse);

        yield return new WaitForSeconds(Mathf.Max(0.01f, t.pulseDuration));

        // 恢复到脉冲前的连续映射值
        t.melody.SetRate01(before);
    }

    // === OSC ===
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

        GUILayout.BeginArea(new Rect(Screen.width - 360, 10, 350, 280), GUI.skin.box);
        GUILayout.Label("=== Hands → Multi-MIDI (连续+随机脉冲) ===");
        GUILayout.Label($"SC: {(isConnected ? "✓" : "✗")} {supercolliderIP}:{supercolliderPort}  {oscAddress}");
        GUILayout.Label($"平滑: {distanceSmoothing:F2}  范围[{normMin:F2},{normMax:F2}]");
        GUILayout.Label($"边缘触发: 间隔{minSendInterval:F3}s  阈{changeThreshold:F3}  迟滞{hysteresisBand:F3}");
        GUILayout.Label($"双手: 左{(hasLeft ? "✓" : "✗")} 右{(hasRight ? "✓" : "✗")} 稳定帧:{bothHandsStableCounter}");
        GUILayout.Label($"距(滤波): {filteredDistance:F4}");
        float n = Mathf.Clamp01(Mathf.InverseLerp(normMin, normMax, filteredDistance));
        GUILayout.Label($"距(0~1): {n:F3}");

        if (targets != null)
        {
            GUILayout.Space(4);
            GUILayout.Label($"Targets: {targets.Length}");
            for (int i = 0; i < targets.Length; i++)
            {
                var t = targets[i];
                if (t == null) continue;
                GUILayout.Label($"- {(t.enabled ? "✓" : "✗")} {t.label} | PB:{(t.controlPitchBend ? "Y" : "N")} Rate:{(t.controlRate ? "Y" : "N")} Timbre:{(t.controlTimbre ? "Y" : "N")}");
            }
        }

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
