using System.Collections;
using UnityEngine;

public class EarthColorController : MonoBehaviour
{
    public Renderer earthRenderer;
    public float duration = 2f;
    public AnimationCurve curve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    static readonly int BlendID = Shader.PropertyToID("_Blend");
    Coroutine co;

    // �� �������ⲿ�ɶ��Ķ���״̬/���� & ����¼�
    public bool IsAnimating { get; private set; }
    public float CurrentBlend { get; private set; }
    public System.Action<float> OnBlendFinished; // ����=Ŀ��ֵ(0=�ָ�,1=�ݽ�)

    public void Deplete() => StartBlend(1f);  // �������ݽ�
    public void Recover() => StartBlend(0f);  // �ݽߡ�����

    // 立即复位为指定混合值（默认回到初始健康=0）
    public void ResetImmediate(float target = 0f)
    {
        if (co != null) { StopCoroutine(co); co = null; }
        var mat = earthRenderer.material;
        CurrentBlend = target;
        if (mat.HasProperty(BlendID)) mat.SetFloat(BlendID, target);
        IsAnimating = false;
        OnBlendFinished?.Invoke(target);
    }

    void StartBlend(float target)
    {
        if (co != null) StopCoroutine(co);
        co = StartCoroutine(BlendTo(target));
    }

    IEnumerator BlendTo(float target)
    {
        var mat = earthRenderer.material; // ����������� material
        float start = mat.HasProperty(BlendID) ? mat.GetFloat(BlendID) : 0f;

        IsAnimating = true;                         // ��
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float k = curve.Evaluate(Mathf.Clamp01(t / duration));
            CurrentBlend = Mathf.Lerp(start, target, k);  // ��
            mat.SetFloat(BlendID, CurrentBlend);
            yield return null;
        }
        CurrentBlend = target;                      // ��
        mat.SetFloat(BlendID, target);

        IsAnimating = false;                        // ��
        OnBlendFinished?.Invoke(target);            // ��
        co = null;
    }
}
