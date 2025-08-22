using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;
using System.Collections;

public class FingerOnPlanetGroupGlobal : MonoBehaviour
{
    [Header("引用")]
    public HolisticTrackingSolution holistic;
    public PanelSwitcher switcher;
    public GameObject planetGroupPanel;
    public GameObject planet1Panel;
    public GameObject planet2Panel;
    public GameObject planet3Panel;

    [Header("判定")]
    public float gestureHoldTime = 0.5f;
    public int requiredStableFrames = 2;
    public float fingerUpThreshold = 0.05f;  // 更严格的竖起判定

    private HolisticTrackingGraph graph;
    private Vector3[] lm = new Vector3[21];
    private bool gotFrame;
    private int frameStable;
    private float holdTimer;
    private int currentCount;

    void Start() { StartCoroutine(Connect()); }

    IEnumerator Connect()
    {
        yield return new WaitForSeconds(1.0f);
        if (holistic == null)
        {
            var go = GameObject.Find("Solution");
            holistic = go ? go.GetComponent<HolisticTrackingSolution>() : null;
        }
        if (holistic == null) { Debug.LogError("[Finger] ❌ 没找到 Holistic"); yield break; }

        var f = typeof(HolisticTrackingSolution).GetField("graphRunner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        graph = f?.GetValue(holistic) as HolisticTrackingGraph;
        if (graph == null) { Debug.LogError("[Finger] ❌ 没找到 Graph"); yield break; }

        graph.OnRightHandLandmarksOutput += OnHand;
        graph.OnLeftHandLandmarksOutput  += OnHand;
        InvokeRepeating(nameof(Analyze), 0.1f, 0.1f);

        Debug.Log("[Finger] ✅ 已连接到 Holistic");
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
        gotFrame = true;
    }

    void Analyze()
    {
        if (switcher == null || switcher.current != planetGroupPanel) return;
        if (!gotFrame) return;

        int count = DetectCount(lm);

        if (count > 0)
        {
            if (count == currentCount)
            {
                frameStable++;
                holdTimer += 0.1f;

                if (frameStable >= requiredStableFrames && holdTimer >= gestureHoldTime)
                {
                    Debug.Log($"[Finger] ✅ 识别到手指数={count}");

                    if (count == 1 && planet1Panel) switcher.SwitchTo(planet1Panel);
                    else if (count == 2 && planet2Panel) switcher.SwitchTo(planet2Panel);
                    else if (count == 3 && planet3Panel) switcher.SwitchTo(planet3Panel);

                    frameStable = 0; holdTimer = 0f; currentCount = 0;
                }
            }
            else
            {
                currentCount = count;
                frameStable = 1;
                holdTimer = 0.1f;
            }
        }
        else
        {
            frameStable = 0;
            holdTimer = 0f;
            currentCount = 0;
        }

        gotFrame = false;
    }

    int DetectCount(Vector3[] a)
    {
        try
        {
            int c = 0;
            // 四指：tip 比 mcp 高（Y 更小）才算竖直
            if (a[5].y - a[8].y  > fingerUpThreshold) c++; // index
            if (a[9].y - a[12].y > fingerUpThreshold) c++; // middle
            if (a[13].y - a[16].y > fingerUpThreshold) c++; // ring
            if (a[17].y - a[20].y > fingerUpThreshold) c++; // pinky

            Debug.Log($"[Finger] IndexUp={a[5].y - a[8].y:F2}, MiddleUp={a[9].y - a[12].y:F2}, RingUp={a[13].y - a[16].y:F2}, PinkyUp={a[17].y - a[20].y:F2} -> Count={c}");

            return c;
        }
        catch { return 0; }
    }
}
