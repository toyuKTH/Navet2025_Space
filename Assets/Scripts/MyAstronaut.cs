// HelmetOverlay2D.cs  — 仅头盔 2D 覆盖（稳定连接 HolisticTrackingGraph 版）
using UnityEngine;
using Mediapipe;                // 必须，有 Packet<T>.Get(...) 扩展
using UI = UnityEngine.UI;
using UColor = UnityEngine.Color;
using URect  = UnityEngine.Rect;
using MP       = Mediapipe;
using MPUnity  = Mediapipe.Unity;
using MPH      = Mediapipe.Unity.Sample.Holistic;

[DefaultExecutionOrder(1000)]  // 确保在 Solution 之后再初始化（更容易连上）
public class HelmetOverlay2D : MonoBehaviour
{
    [Header("容器")]
    public RectTransform container;
    public Canvas overlayCanvas;

    [Header("头盔 UI")]
    public UI.Image helmetImage;
    public Vector2  helmetBaseSize = new Vector2(240, 240);
    public UColor   helmetTint     = UColor.white;

    [Header("跟随 & 适配")]
    public bool   mirrorHorizontally = true;
    public Vector2 positionOffset    = new Vector2(0, 80f);
    public float  smoothSpeed        = 8f;
    public float  earWidthScale      = 0.9f;
    public float  minScale = 0.4f, maxScale = 2.5f;

    [Header("调试")]
    public bool showDebug = true;

    // —— 关键：直接暴露一个 Graph，推荐在 Inspector 里把 Solution 节点上的 HolisticTrackingGraph 拖进来
    public MPH.HolisticTrackingGraph graph;  

    // 状态
    bool connected, hasValid;
    readonly Vector2[] uv = new Vector2[33];
    Vector2 targetPos, currentPos;
    float targetScale = 1f, currentScale = 1f;
    int frameCount, dataCount;

    const int NOSE=0, L_EAR=7, R_EAR=8;

    void Start() => StartCoroutine(SetupUIAndConnect());
    void OnDestroy()
    {
        if (graph != null) graph.OnPoseLandmarksOutput -= OnPoseLandmarks2D;
    }

    System.Collections.IEnumerator SetupUIAndConnect()
    {
        // 1) 准备 UI （同之前）
        if (!container)
        {
            if (!overlayCanvas)
            {
                var go = new GameObject("HelmetCanvas",
                    typeof(Canvas), typeof(UI.CanvasScaler), typeof(UI.GraphicRaycaster));
                overlayCanvas = go.GetComponent<Canvas>();
                overlayCanvas.renderMode   = RenderMode.ScreenSpaceOverlay;
                overlayCanvas.sortingOrder = 200;
                var scaler = go.GetComponent<UI.CanvasScaler>();
                scaler.uiScaleMode         = UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920,1080);
                container = overlayCanvas.transform as RectTransform;
            }
            else container = overlayCanvas.transform as RectTransform;
        }

        if (!helmetImage)
        {
            var go = new GameObject("AstronautHelmet", typeof(RectTransform), typeof(UI.Image));
            go.transform.SetParent(container, false);
            helmetImage = go.GetComponent<UI.Image>();
            helmetImage.color = helmetTint; helmetImage.raycastTarget = false;
            var rt = helmetImage.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f,0.5f);
            rt.sizeDelta = helmetBaseSize; rt.anchoredPosition = Vector2.zero;
            helmetImage.sprite = CreateHelmetSprite();
        }

        // 2) —— 稳定连接 —— 优先使用 Inspector 拖的 graph；否则自动查找
        float timeLimit = 5f; // 最多等 5 秒
        float t = 0f;
        while (graph == null && t < timeLimit)
        {
            graph = FindObjectOfType<MPH.HolisticTrackingGraph>(true);
            if (graph != null) break;
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (graph == null)
        {
            Debug.LogError("[HelmetOverlay2D] 没找到 HolisticTrackingGraph（请把 Solution 节点上的组件拖到脚本的 graph 字段）");
            yield break;
        }

        // 3) 等 Graph 进入运行状态（StartRun 之后才会持续推数据）
        // 简易做法：看一小段时间是否能收到任何 Landmark 包，否则持续重试绑定一次
        bool subscribed = false;
        void Subscribe()
        {
            if (subscribed) return;
            graph.OnPoseLandmarksOutput += OnPoseLandmarks2D;
            subscribed = true;
            connected  = true;
        }
        Subscribe();   // 先订一次

        // 有些版本 StartRun 在下一帧甚至更晚，这里宽松地再等几帧
        for (int i=0;i<30;i++) yield return null;
    }

    // 2D 关键点（0..1）：x 右、y 下（这里把 y 翻上去）
    void OnPoseLandmarks2D(object sender, MPUnity.OutputStream<MP.NormalizedLandmarkList>.OutputEventArgs e)
    {
        var list = e.packet == null ? null : e.packet.Get(MP.NormalizedLandmarkList.Parser);
        if (list == null || list.Landmark == null || list.Landmark.Count < 9)
        {
            hasValid = false;
            return;
        }

        for (int i = 0; i < 33 && i < list.Landmark.Count; i++)
        {
            var lm = list.Landmark[i];
            float x = lm.X, y = lm.Y;
            if (mirrorHorizontally) x = 1f - x;
            y = 1f - y;
            uv[i] = new Vector2(x, y);
        }

        hasValid = true;
        dataCount++;
        UpdateHelmetTarget();
    }

    void UpdateHelmetTarget()
    {
        Vector2 lEar = uv[L_EAR], rEar = uv[R_EAR];
        if (lEar == Vector2.zero || rEar == Vector2.zero)
        {
            var nose = uv[NOSE];
            lEar = nose + new Vector2(-0.05f,0);
            rEar = nose + new Vector2( 0.05f,0);
        }

        var earMid = (lEar + rEar) * 0.5f;
        float earDist01 = Mathf.Max(0.0001f, Vector2.Distance(lEar,rEar));

        Vector2 size = GetContainerSize();
        var centered = earMid - new Vector2(0.5f,0.5f);
        targetPos = new Vector2(centered.x*size.x, centered.y*size.y) + positionOffset;

        float pixelW   = earDist01 * size.x * earWidthScale;
        float baseW    = Mathf.Max(helmetBaseSize.x, 1f);
        targetScale    = Mathf.Clamp(pixelW / baseW, minScale, maxScale);
    }

    Vector2 GetContainerSize()
    {
        if (container) return container.rect.size;
        if (overlayCanvas)
        {
            var scaler = overlayCanvas.GetComponent<UI.CanvasScaler>();
            if (scaler && scaler.uiScaleMode == UI.CanvasScaler.ScaleMode.ScaleWithScreenSize)
                return scaler.referenceResolution;
            var r = overlayCanvas.pixelRect;
            return new Vector2(r.width, r.height);
        }
        return new Vector2(UnityEngine.Screen.width, UnityEngine.Screen.height);
    }

    void Update()
    {
        frameCount++;
        if (!helmetImage || !hasValid) return;
        currentPos   = Vector2.Lerp(currentPos,   targetPos,   Time.deltaTime * smoothSpeed);
        currentScale = Mathf.Lerp(currentScale,   targetScale, Time.deltaTime * smoothSpeed);
        var rt = helmetImage.rectTransform;
        rt.anchoredPosition = currentPos;
        rt.localScale       = Vector3.one * currentScale;
    }

    // 简易占位贴图（换成你的 PNG 更好看）
    UnityEngine.Sprite CreateHelmetSprite()
    {
        int w=256,h=256;
        var tex=new Texture2D(w,h,TextureFormat.RGBA32,false);
        var px=new UColor[w*h];
        for (int i=0;i<px.Length;i++) px[i]=UColor.clear;
        int cx=w/2, cy=(int)(h*0.62f);
        UColor shell=new UColor(0.85f,0.9f,1f,0.9f);
        UColor glass=new UColor(0.1f,0.1f,0.2f,0.85f);
        UColor ring =new UColor(0.7f,0.8f,1f,1f);
        DrawCircle(px,w,h,cx,cy,96,shell);
        DrawCircle(px,w,h,cx,cy,80,glass);
        DrawRect  (px,w,h,cx-60,cy-100,120,24,ring);
        tex.SetPixels(px); tex.Apply();
        return UnityEngine.Sprite.Create(tex,new URect(0,0,w,h),new Vector2(0.5f,0.5f),100f);
    }
    void DrawCircle(UColor[] px,int tw,int th,int cx,int cy,int r,UColor c){
        for(int y=-r;y<=r;y++) for(int x=-r;x<=r;x++){
            if(x*x+y*y>r*r) continue; int X=cx+x,Y=cy+y;
            if(X<0||X>=tw||Y<0||Y>=th) continue; px[Y*tw+X]=c; } }
    void DrawRect(UColor[] px,int tw,int th,int sx,int sy,int rw,int rh,UColor c){
        for(int y=0;y<rh;y++) for(int x=0;x<rw;x++){
            int X=sx+x,Y=sy+y; if(X<0||X>=tw||Y<0||Y>=th) continue; px[Y*tw+X]=c; } }

    // 调试面板
    void OnGUI(){
        if(!showDebug) return;
        GUILayout.BeginArea(new URect(10,10,320,160), GUI.skin.box);
        GUILayout.Label("HelmetOverlay2D");
        GUILayout.Label($"Connected: {connected}  (已订阅事件: {(graph!=null)})");
        GUILayout.Label($"ValidLandmarks: {hasValid}");
        GUILayout.Label($"Frame: {frameCount}  Data: {dataCount}");
        if(GUILayout.Button($"镜像: {(mirrorHorizontally ? "是":"否")}")) mirrorHorizontally=!mirrorHorizontally;
        GUILayout.EndArea();
    }
}
