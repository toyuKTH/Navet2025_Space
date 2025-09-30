using System.Collections;
using UnityEngine;

public class WorldHealthCoordinator : MonoBehaviour
{
    [Header("Refs")]
    public TreeSpawnerOnSphere trees;      // ��ľ����/��տ���
    public EarthColorController earth;     // ������ɫ���ƣ��躬 IsAnimating / duration��

    [Header("Behavior")]
    [Tooltip("���ڹ���ʱ�ٴε����Ƿ�������")]
    public bool interruptRunningFlow = true;
    [Tooltip("���յȴ�������߽�֡���룩")]
    public float extraEarthWait = 0.1f;

    [Header("Anti-flap / ����")]
    [Tooltip("������ת֮�����С������룩�����̻����")]
    public float minInterval = 0.0f;

    Coroutine flow;
    float lastTransitionAt = -999f;

    [Header("Idle Auto-Recover")]
    [Tooltip("超过该秒数未收到任何用户动作，将自动触发 GoHealthy()")] public float idleToHealthySeconds = 5f;
    public bool enableIdleAutoHealthy = true;
    float lastActionAt = -999f;
    bool idleTriggered = false;

    [Header("Derive from EarthColorController")]
    [Range(0f, 1f)] public float healthyBlendThreshold = 0.02f; // CurrentBlend <= 阈值 且不在动画中 → 认为健康

    public enum Mode { Healthy, Depleted, Transitioning }
    public Mode Current { get; private set; } = Mode.Healthy;

    // ���� ������� ���� //
    public void GoHealthy() { StartFlow(FlowHealthy()); }   // ��ã�����ա�����ָ�������
    public void GoDepleted() { StartFlow(FlowDepleted()); }  // �仵������ա�����ݽ�

    // 外部上报：用户发生了一个有效动作（挥手、比耶等）
    public void NotifyUserAction()
    {
        lastActionAt = Time.time;
        idleTriggered = false; // 重置一次性触发门控
    }

    void StartFlow(IEnumerator co)
    {
        if (Time.time - lastTransitionAt < minInterval) return;
        lastTransitionAt = Time.time;

        if (flow != null)
        {
            if (!interruptRunningFlow) return;
            StopCoroutine(flow);
        }
        flow = StartCoroutine(Run(co));
    }

    IEnumerator Run(IEnumerator co)
    {
        Current = Mode.Transitioning;
        yield return StartCoroutine(co);
        flow = null;
    }

    void Start()
    {
        lastActionAt = Time.time;
        StartCoroutine(IdleWatchdog());
    }

    IEnumerator IdleWatchdog()
    {
        var wait = new WaitForSeconds(0.2f);
        while (true)
        {
            if (enableIdleAutoHealthy && !idleTriggered && Time.time - lastActionAt >= idleToHealthySeconds)
            {
                GoHealthy();
                idleTriggered = true; // 避免在下一帧再次触发，直到收到新的动作
            }
            yield return wait;
        }
    }

    // ========== �仵 ========== //
    IEnumerator FlowDepleted()
    {
        // 1) 若当前有树，则逐渐清除；如果已经没有树，则不做无谓操作
        if (trees != null && !trees.AllCleared)
        {
            trees.BeginDisappear();
            if (!trees.AllCleared)
            {
                yield return new WaitUntil(() => trees.AllCleared);
            }
        }

        // 2) ����仵
        if (earth != null)
        {
            earth.Deplete();
            yield return WaitEarthDone();
        }

        Current = Mode.Depleted;
    }

    // ========== ��� ========== //
    IEnumerator FlowHealthy()
    {
        // 若地球已处于健康颜色：不清树、不恢复颜色，直接保持/继续长树
        if (IsEarthHealthy())
        {
            if (trees != null)
            {
                trees.BeginGrow();
            }
            Current = Mode.Healthy;
            yield break;
        }

        // 1) 地球未健康时，先保证把现有的树清理干净
        if (trees != null && !trees.AllCleared)
        {
            trees.BeginDisappear();
            if (!trees.AllCleared)
            {
                yield return new WaitUntil(() => trees.AllCleared);
            }
        }

        // 2) ����ָ�
        if (earth != null)
        {
            earth.Recover();
            yield return WaitEarthDone();
        }

        // 3) �ٿ�ʼ����
        if (trees != null)
        {
            trees.BeginGrow();
        }

        Current = Mode.Healthy;
    }

    bool IsEarthHealthy()
    {
        if (earth == null) return true;
        if (earth.IsAnimating) return false;
        return earth.CurrentBlend <= Mathf.Clamp01(healthyBlendThreshold);
    }

    // ������ɫ�����ȴ��������� IsAnimating��û�оͰ� duration �ȣ�
    IEnumerator WaitEarthDone()
    {
        if (earth != null)
        {
            float safety = earth.duration + extraEarthWait;
            float t = 0f;
            while (earth.IsAnimating && t < safety)
            {
                t += Time.deltaTime;
                yield return null;
            }
            if (t >= safety) yield return new WaitForSeconds(extraEarthWait);
        }
        else
        {
            yield return null;
        }
    }
}
