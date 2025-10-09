using System;
using System.Collections;
using UnityEngine;

public class WorldHealthCoordinator : MonoBehaviour
{
    [Header("Refs")]
    public TreeSpawnerOnSphere trees;
    public EarthColorController earth;

    [Header("Behavior")]
    [Tooltip("在流程运行时再次触发是否打断当前流程")]
    public bool interruptRunningFlow = true;
    [Tooltip("地球颜色动画结束后额外等待（秒）")]
    public float extraEarthWait = 0.1f;

    [Header("Anti-flap / 防抖")]
    [Tooltip("两次状态流转之间的最小间隔（秒）")]
    public float minInterval = 0.0f;

    Coroutine flow;
    float lastTransitionAt = -999f;

    [Header("Idle Auto-Recover")]
    [Tooltip("超过该秒数未收到任何用户动作，将自动触发 GoHealthy()")]
    public float idleToHealthySeconds = 5f;
    public bool enableIdleAutoHealthy = true;
    float lastActionAt = -999f;
    bool idleTriggered = false;

    [Header("Derive from EarthColorController")]
    [Range(0f, 1f)]
    public float healthyBlendThreshold = 0.02f; // CurrentBlend <= 阈值 且不在动画中 → 认为健康

    public enum Mode { Healthy, Depleted, Transitioning }
    public Mode Current { get; private set; } = Mode.Healthy;

    // ===== 对外事件 =====
    /// <summary>用户发生了有效动作（挥手、✌️等）</summary>
    public event Action OnUserAction;
    /// <summary>闲置达到阈值时（系统自动恢复前）触发的事件</summary>
    public event Action OnIdleAutoHealthy;

    /// <summary>最近一次用户动作时间（秒）。外部可写入以刷新计时并清 idle 门控。</summary>
    public float LastActionTime
    {
        get => lastActionAt;
        set
        {
            lastActionAt = value;
            idleTriggered = false; // 外部刷新视为新“动作周期”，解除一次性门控
        }
    }

    // ===== 外部 API（状态切换） =====
    public void GoHealthy() { StartFlow(FlowHealthy()); }
    public void GoDepleted() { StartFlow(FlowDepleted()); }

    /// <summary>外部上报：用户发生了一个有效动作（等价于 MarkUserActionNow）。</summary>
    public void NotifyUserAction() => MarkUserActionNow();

    /// <summary>把“最近动作时间”更新为现在，并广播 OnUserAction。</summary>
    public void MarkUserActionNow()
    {
        lastActionAt = Time.time;
        idleTriggered = false; // 重置一次性触发门控
        OnUserAction?.Invoke();
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
        lastActionAt = Time.time; // 启动时视为刚有过动作，避免“秒触发”
        StartCoroutine(IdleWatchdog());
    }

    IEnumerator IdleWatchdog()
    {
        var wait = new WaitForSeconds(0.2f);
        while (true)
        {
            if (enableIdleAutoHealthy && !idleTriggered && Time.time - lastActionAt >= idleToHealthySeconds)
            {
                // 先广播：供上层（如 FistGestureController）做音频/动画路由
                OnIdleAutoHealthy?.Invoke();

                // 立即更新最后动作时间与一次性门控，避免下一帧重复触发
                lastActionAt = Time.time;
                idleTriggered = true;

                // 再执行自动恢复流程
                GoHealthy();
            }
            yield return wait;
        }
    }

    // ========== 枯萎流程 ==========
    IEnumerator FlowDepleted()
    {
        // 1) 若当前有树，则逐渐清除
        if (trees != null && !trees.AllCleared)
        {
            trees.BeginDisappear();
            if (!trees.AllCleared)
            {
                yield return new WaitUntil(() => trees.AllCleared);
            }
        }

        // 2) 地球去色/枯萎
        if (earth != null)
        {
            earth.Deplete();
            yield return WaitEarthDone();
        }

        Current = Mode.Depleted;
    }

    // ========== 健康流程 ==========
    IEnumerator FlowHealthy()
    {
        // 若地球已处于健康颜色：不清树、不恢复颜色，直接继续长树
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

        // 2) 地球恢复
        if (earth != null)
        {
            earth.Recover();
            yield return WaitEarthDone();
        }

        // 3) 再开始长树
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

    // 等待地球颜色动画结束（带安全时间）
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

    /// <summary>
    /// 立即重置至初始状态：清空树、地球恢复为健康(Blend=0)、同步内部状态。
    /// 也会把最近动作时间设为现在、清 idle 门控，避免重置后立刻触发闲置逻辑。
    /// </summary>
    public void ResetToInitial(bool alsoStopFlows = true)
    {
        if (alsoStopFlows && flow != null)
        {
            StopCoroutine(flow);
            flow = null;
        }

        // 清空树
        if (trees != null)
        {
            trees.ForceClearNow();
        }

        // 地球恢复为健康
        if (earth != null)
        {
            earth.ResetImmediate(0f);
        }

        // 重置内部门控/状态
        Current = Mode.Healthy;
        lastTransitionAt = -999f;
        lastActionAt = Time.time; // 重置后起一个“新周期”
        idleTriggered = false;
    }
}
