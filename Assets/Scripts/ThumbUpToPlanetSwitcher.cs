using System.Collections;
using UnityEngine;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.Holistic;

public class ThumbUpToPlanetSwitcher : MonoBehaviour
{
    public HolisticTrackingSolution holistic;

    [Header("面板引用")]
    public GameObject welcomePanel;
    public GameObject targetPanel;

    [Header("手势判定")]
    public float gestureHoldTime = 1.0f;
    public int requiredStableFrames = 3;

    private Vector3[] currentLandmarks = new Vector3[21];
    private bool hasHandData = false;
    private bool landmarksUpdated = false;
    private int thumbsUpStableCount = 0;
    private bool hasTriggered = false;
    private float thumbsUpTimer = 0f;

    void Start()
    {
        StartCoroutine(ConnectToHandDetection());
    }

    IEnumerator ConnectToHandDetection()
    {
        yield return new WaitForSeconds(1.5f);
        var solution = GameObject.Find("Solution");
        var h = solution?.GetComponent<HolisticTrackingSolution>();
        var runnerField = h?.GetType().GetField("graphRunner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var runner = runnerField?.GetValue(h) as HolisticTrackingGraph;

        if (runner != null)
            runner.OnRightHandLandmarksOutput += OnRightHandLandmarksReceived;

        InvokeRepeating(nameof(AnalyzeGesture), 0.1f, 0.1f);
    }

    void OnRightHandLandmarksReceived(object stream, OutputStream<NormalizedLandmarkList>.OutputEventArgs e)
    {
        var landmarks = e.packet?.Get(NormalizedLandmarkList.Parser);
        if (landmarks == null || landmarks.Landmark.Count < 21) return;

        for (int i = 0; i < 21; i++)
        {
            var lm = landmarks.Landmark[i];
            currentLandmarks[i] = new Vector3(lm.X, lm.Y, lm.Z);
        }
        hasHandData = true;
        landmarksUpdated = true;
    }

    void AnalyzeGesture()
    {
        if (!welcomePanel.activeSelf || hasTriggered || !landmarksUpdated || !hasHandData)
            return;

        Vector3 thumbTip = currentLandmarks[4];
        Vector3 indexTip = currentLandmarks[8];
        Vector3 wrist = currentLandmarks[0];

        bool isThumbUp = (wrist.y - thumbTip.y) > 0.08f && thumbTip.y < indexTip.y - 0.04f;

        if (isThumbUp)
        {
            thumbsUpStableCount++;
            if (thumbsUpStableCount >= requiredStableFrames)
            {
                thumbsUpTimer += 0.1f;
                if (thumbsUpTimer >= gestureHoldTime)
                    TriggerTargetPanel();
            }
        }
        else
        {
            thumbsUpStableCount = 0;
            thumbsUpTimer = 0;
        }
        landmarksUpdated = false;
    }

    void TriggerTargetPanel()
    {
        hasTriggered = true;

        // 调用 PanelSwitcher
        PanelSwitcher switcher = FindObjectOfType<PanelSwitcher>();
        if (switcher != null)
        {
            switcher.SwitchPanel(welcomePanel, targetPanel);
        }
        else
        {
            // 没有管理器就直接切
            welcomePanel.SetActive(false);
            targetPanel.SetActive(true);
        }

        Debug.Log("👍 Thumb Up，触发面板跳转");
    }
}
