using System.Collections;
using UnityEngine;

public class EarthColorController : MonoBehaviour
{
    public Renderer earthRenderer;
    public float duration = 2f;
    public AnimationCurve curve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    static readonly int BlendID = Shader.PropertyToID("_Blend");
    Coroutine co;

    // ★ 新增：外部可读的动画状态/进度 & 完成事件
    public bool IsAnimating { get; private set; }
    public float CurrentBlend { get; private set; }
    public System.Action<float> OnBlendFinished; // 参数=目标值(0=恢复,1=枯竭)

    public void Deplete() => StartBlend(1f);  // 健康→枯竭
    public void Recover() => StartBlend(0f);  // 枯竭→健康

    void StartBlend(float target)
    {
        if (co != null) StopCoroutine(co);
        co = StartCoroutine(BlendTo(target));
    }

    IEnumerator BlendTo(float target)
    {
        var mat = earthRenderer.material; // 单个地球可用 material
        float start = mat.HasProperty(BlendID) ? mat.GetFloat(BlendID) : 0f;

        IsAnimating = true;                         // ★
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float k = curve.Evaluate(Mathf.Clamp01(t / duration));
            CurrentBlend = Mathf.Lerp(start, target, k);  // ★
            mat.SetFloat(BlendID, CurrentBlend);
            yield return null;
        }
        CurrentBlend = target;                      // ★
        mat.SetFloat(BlendID, target);

        IsAnimating = false;                        // ★
        OnBlendFinished?.Invoke(target);            // ★
        co = null;
    }
}
