using UnityEngine;
using UnityEngine.UI;

// 起别名，避免与 UnityEngine.Screen 冲突
using MP = Mediapipe.Unity;
using HolisticNS = Mediapipe.Unity.Sample.Holistic;

[RequireComponent(typeof(RawImage))]
public class MirrorMediapipeToRawImage : MonoBehaviour
{
    [Tooltip("场景中的 HolisticTrackingSolution 组件")]
    public HolisticNS.HolisticTrackingSolution solution;

    [Tooltip("要显示到的 RawImage，不填就用当前物体")]
    public RawImage targetRawImage;

    [Tooltip("是否拷贝原 Screen 的显示参数")]
    public bool copyScreenSettings = true;

    void Awake()
    {
        if (!targetRawImage) targetRawImage = GetComponent<RawImage>();
    }

    void Start()
    {
        // 1) 找到 Solution
        if (!solution)
            solution = FindObjectOfType<HolisticNS.HolisticTrackingSolution>(true);
        if (!solution)
        {
            Debug.LogError("[MirrorMediapipeToRawImage] 未找到 HolisticTrackingSolution");
            return;
        }

        // 2) 找到原来的 Screen（Annotatable Screen 上那个）
        MP.Screen originalScreen = solution.screen;                 // 大多数版本有这个字段
        if (!originalScreen) originalScreen = FindObjectOfType<MP.Screen>(true);
        if (!originalScreen)
        {
            Debug.LogError("[MirrorMediapipeToRawImage] 未找到原始 Screen");
            return;
        }

        // 3) 确保自己这边也有 MP.Screen 组件
        var myScreen = targetRawImage.GetComponent<MP.Screen>();
        if (!myScreen) myScreen = targetRawImage.gameObject.AddComponent<MP.Screen>();

        // 4) 复用同一份 ImageSource（同一台摄像头）
        myScreen.imageSource = originalScreen.imageSource;

        // 5) 可选：拷贝显示参数
        if (copyScreenSettings)
        {
            myScreen.keepAspectRatio   = originalScreen.keepAspectRatio;
            myScreen.fit               = originalScreen.fit;
            myScreen.rotation          = originalScreen.rotation;
            myScreen.flipHorizontally  = originalScreen.flipHorizontally;
            myScreen.flipVertically    = originalScreen.flipVertically;
        }

        myScreen.RequestResize();
    }
}
