using System.Collections;
using UnityEngine;

public class WorldHealthCoordinator : MonoBehaviour
{
    [Header("Refs")]
    public TreeSpawnerOnSphere trees;      // 你的种树脚本
    public EarthColorController earth;     // 颜色控制脚本（含 IsAnimating）

    [Header("Behavior")]
    public bool interruptRunningFlow = true; // 正在过渡时再次调用是否打断重来
    public float extraEarthWait = 0.1f;      // 保险等待，避免边界帧

    Coroutine flow;
    public enum Mode { Healthy, Depleted, Transitioning }
    public Mode Current { get; private set; } = Mode.Healthy;

    // —— 对外只暴露两个方法 ——
    public void GoHealthy() { StartFlow(FlowHealthy()); }   // 变好：树清空→地球恢复→再种
    public void GoDepleted() { StartFlow(FlowDepleted()); }  // 变坏：树清空→地球枯竭

    void StartFlow(IEnumerator co)
    {
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

    // ========== 流程：变坏 ==========
    IEnumerator FlowDepleted()
    {
        // 1) 停止新增，清空树
        trees.BeginDisappear();
        if (!trees.AllCleared)
        {
            trees.BeginDisappear();
            yield return new WaitUntil(() => trees.AllCleared);
        }

        // 2) 地球变坏（蓝->紫, 绿->黄）
        earth.Deplete();
        yield return WaitEarthDone();

        Current = Mode.Depleted;
    }

    // ========== 流程：变好 ==========
    IEnumerator FlowHealthy()
    {
        // 1) 停止新增，清空树（保持规则一致）
        trees.BeginDisappear();
        if (!trees.AllCleared)
        {
            trees.BeginDisappear();
            yield return new WaitUntil(() => trees.AllCleared);
        }

        // 2) 地球恢复
        earth.Recover();
        yield return WaitEarthDone();

        // 3) 再开始种树
        trees.BeginGrow();

        Current = Mode.Healthy;
    }

    // 地球颜色动画等待（优先用 IsAnimating；没有就按时长等）
    IEnumerator WaitEarthDone()
    {
        if (earth != null)
        {
            // 如果你按我之前的改法，EarthColorController 有 IsAnimating
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
