using System;
using System.Collections;
using UnityEngine;

public class TreeGrower : MonoBehaviour
{
    [Header("动画时长")]
    public float growTime = 1.5f;
    public float shrinkTime = 1.2f;
    public AnimationCurve growCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);   // ↑ 从0到1
    public AnimationCurve shrinkCurve = AnimationCurve.EaseInOut(0, 0, 1, 1); // ↑ 从0到1

    public enum AnimState { Hidden, Growing, Shown, Shrinking }
    public AnimState State { get; private set; } = AnimState.Hidden;

    public bool IsFullyHidden => State == AnimState.Hidden;
    public bool IsFullyShown => State == AnimState.Shown;

    public event Action<TreeGrower> OnFullyHidden;   // 完全消失时触发


    [SerializeField] Vector3 baseScale = Vector3.one; // 作为prefab的基准尺寸
    Vector3 targetScale = Vector3.one;
    Coroutine co;

    void Awake()
    {
        if (baseScale == Vector3.one) baseScale = transform.localScale == Vector3.zero ? Vector3.one : transform.localScale;
    }


    public void SetTargetScale(Vector3 s) { targetScale = s; if (State == AnimState.Shown) transform.localScale = s; }
    public void SetSizeMultiplier(float m) => SetTargetScale(baseScale * m);
    public void SetSizeMultiplierWorld(float m, Transform parent)
    {
        var p = parent ? parent.lossyScale : Vector3.one;
        SetTargetScale(new Vector3(baseScale.x * m / (p.x == 0 ? 1 : p.x),
                                   baseScale.y * m / (p.y == 0 ? 1 : p.y),
                                   baseScale.z * m / (p.z == 0 ? 1 : p.z)));
    }

    public void Appear()
    {
        if (co != null) StopCoroutine(co);
        co = StartCoroutine(GrowCo());
    }
    public void Disappear()
    {
        if (co != null) StopCoroutine(co);
        co = StartCoroutine(ShrinkCo());
    }

    IEnumerator GrowCo()
    {                           // 小 → 大
        State = AnimState.Growing;
        float t = 0f;
        while (t < growTime)
        {
            t += Time.deltaTime;
            float k = growCurve.Evaluate(Mathf.Clamp01(t / growTime)); // 0→1
            transform.localScale = Vector3.LerpUnclamped(Vector3.zero, targetScale, k);
            yield return null;
        }
        transform.localScale = targetScale;
        State = AnimState.Shown;
        co = null;
    }

    IEnumerator ShrinkCo()
    {                         // 大 → 小 → 隐藏
        State = AnimState.Shrinking;
        Vector3 start = transform.localScale;
        float t = 0f;
        while (t < shrinkTime)
        {
            t += Time.deltaTime;
            float k = shrinkCurve.Evaluate(Mathf.Clamp01(t / shrinkTime)); // 0→1
            transform.localScale = Vector3.LerpUnclamped(start, Vector3.zero, k);
            yield return null;
        }
        transform.localScale = Vector3.zero;
        State = AnimState.Hidden;
        co = null;
        OnFullyHidden?.Invoke(this);
    }

    // 给对象池用的便捷方法（可选）
    public int poolIndex;           // 由池填
    public TreePool pool;           // 由池填
    public void ReturnToPool() { pool?.Return(this); }
}
