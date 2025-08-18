using UnityEngine;
using UnityEngine.UI;

/// 挂在显示摄像头画面的 RawImage 上
[RequireComponent(typeof(RawImage))]
public class AutoFit : MonoBehaviour
{
    public enum FitMode { FitInside, FitCover, FitWidth, FitHeight }

    [SerializeField] FitMode mode = FitMode.FitInside;

    RawImage raw;
    RectTransform rt, parent;

    void Awake()
    {
        raw = GetComponent<RawImage>();
        rt = GetComponent<RectTransform>();
        parent = rt.parent as RectTransform;
        // 建议锚点在中心，便于观察：Anchors = (0.5,0.5), Pivot = (0.5,0.5)
    }

    void LateUpdate()
    {
        if (!raw || !rt || !parent) return;

        var tex = raw.texture;
        if (!tex) return;

        float texW = tex.width;
        float texH = tex.height;
        Vector2 p = parent.rect.size;

        float wRatio = p.x / texW;
        float hRatio = p.y / texH;

        float ratio =
            mode == FitMode.FitCover  ? Mathf.Max(wRatio, hRatio) :
            mode == FitMode.FitWidth  ? wRatio :
            mode == FitMode.FitHeight ? hRatio :
                                         Mathf.Min(wRatio, hRatio); // FitInside

        // 让 RawImage 按纹理比例缩放
        Vector2 newSize = new Vector2(texW * ratio, texH * ratio);
        rt.sizeDelta = newSize;

        // 正方形容器想铺满不留灰边时，用 UV 裁剪
        if (mode == FitMode.FitCover)
        {
            float uiAspect  = newSize.x / Mathf.Max(1f, newSize.y);
            float texAspect = texW / Mathf.Max(1f, texH);

            if (texAspect > uiAspect)
            {
                // 纹理更宽，裁左右
                float scale = uiAspect / texAspect;
                raw.uvRect = new Rect((1 - scale) * 0.5f, 0, scale, 1);
            }
            else
            {
                // 纹理更高，裁上下
                float scale = texAspect / uiAspect;
                raw.uvRect = new Rect(0, (1 - scale) * 0.5f, 1, scale);
            }
        }
        else
        {
            // 其他模式恢复全图
            raw.uvRect = new Rect(0, 0, 1, 1);
        }
    }
}
