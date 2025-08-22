using UnityEngine;
using System.Collections;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;

public class FistToPanelSwitcherGlobal : MonoBehaviour
{
    public HolisticTrackingSolution holistic;
    public PanelSwitcher switcher;

    [Header("在哪些面板上启用返回手势")]
    public GameObject[] activeOnPanels;   // 例如 Planet1Panel / Planet2Panel / Planet3Panel

    [Header("判定与冷却")]
    public float gestureHoldTime = 1.0f;   // 握拳保持多久触发
    public int requiredStableFrames = 3;   // 稳定帧数
    public float cooldownSeconds = 5f;     // 触发后冷却

    private HolisticTrackingGraph graph;
    private Vector3[] lm = new Vector3[21];
    private bool hasData, updated;
    private int stableCount;
    private float holdTimer;
    private bool locked;

    void Start() { StartCoroutine(Connect()); }

    IEnumerator Connect()
    {
        yield return new WaitForSeconds(1.0f);
        if (holistic == null)
        {
            var go = GameObject.Find("Solution");
            holistic = go ? go.GetComponent<HolisticTrackingSolution>() : null;
        }
        if (holistic == null) { Debug.LogError("[Fist] no Holistic"); yield break; }

        var f = typeof(HolisticTrackingSolution).GetField("graphRunner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        graph = f?.GetValue(holistic) as HolisticTrackingGraph;
        if (graph == null) { Debug.LogError("[Fist] no Graph"); yield break; }

        graph.OnRightHandLandmarksOutput += OnHand;
        graph.OnLeftHandLandmarksOutput  += OnHand;
        InvokeRepeating(nameof(Analyze), 0.1f, 0.1f);
    }

    void OnDestroy()
    {
        if (graph != null)
        {
            graph.OnRightHandLandmarksOutput -= OnHand;
            graph.OnLeftHandLandmarksOutput  -= OnHand;
        }
        CancelInvoke(nameof(Analyze));
    }

    void OnHand(object _, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var list = e.packet?.Get(NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark.Count < 21) return;

        for (int i = 0; i < 21; i++)
        {
            var p = list.Landmark[i];
            lm[i] = new Vector3(p.X, p.Y, p.Z);
        }
        hasData = true;
        updated = true;
    }

    bool IsActivePanel()
    {
        if (switcher == null || switcher.current == null || activeOnPanels == null) return false;
        foreach (var p in activeOnPanels) if (p == switcher.current) return true;
        return false;
    }

    void Analyze()
    {
        if (locked || !updated || !hasData) return;
        if (!IsActivePanel()) return;

        if (DetectFist())
        {
            stableCount++;
            if (stableCount >= requiredStableFrames)
            {
                holdTimer += 0.1f;
                if (holdTimer >= gestureHoldTime)
                {
                    TriggerBack();
                }
            }
        }
        else
        {
            stableCount = 0;
            holdTimer = 0f;
        }
        updated = false;
    }

    bool DetectFist()
    {
        try
        {
            // 四个手指都弯曲（tip 在 MCP 下方）
            bool indexBent  = (lm[8].y > lm[5].y);
            bool middleBent = (lm[12].y > lm[9].y);
            bool ringBent   = (lm[16].y > lm[13].y);
            bool pinkyBent  = (lm[20].y > lm[17].y);

            return indexBent && middleBent && ringBent && pinkyBent;
        }
        catch { return false; }
    }

    void TriggerBack()
    {
        locked = true;
        stableCount = 0; holdTimer = 0f;

        if (switcher != null) switcher.Back();
        Debug.Log("✊ Fist 手势识别 → Back");

        StartCoroutine(Cooldown());
    }

    IEnumerator Cooldown()
    {
        yield return new WaitForSeconds(cooldownSeconds);
        locked = false;
        Debug.Log("🔄 Fist 冷却结束，可再次识别");
    }
}
