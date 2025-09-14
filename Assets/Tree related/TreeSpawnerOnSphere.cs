using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using Random = UnityEngine.Random;


public class TreeSpawnerOnSphere : MonoBehaviour
{
    [Header("引用")]
    public Transform earth;
    public Collider earthCollider;      // SphereCollider/MeshCollider
    public TreePool pool;

    [Header("节奏")]
    public float spawnInterval = 0.5f;      // 生长节奏（越大越慢）
    public float disappearInterval = 0.2f;  // 消失节奏（逐棵依次消失，0=同时）
    public float sizeMultiplier = 0.25f;

    [Header("放置")]
    public float surfaceOffset = 0.02f;
    public float minSurfaceSpacing = 0.8f;
    public float sphereRadius = 5f;
    public Vector2 randomScale = new Vector2(0.8f, 1.2f);
    public int maxActiveTrees = 80;

    public enum Phase { Idle, Growing, Disappearing }
    public Phase CurrentPhase { get; private set; } = Phase.Idle;


    public bool AllCleared { get; private set; } = true;  // 是否已全部消失
    public event Action OnAllCleared;                     // 全部消失回调

    class Active { public TreeGrower g; public Vector3 pos; public Vector3 normal; }
    readonly List<Active> actives = new List<Active>();
    Coroutine growLoop;

    // —— 外部调用 ——
    public void BeginGrow()
    {
        if (CurrentPhase == Phase.Growing) return;
        CurrentPhase = Phase.Growing;
        AllCleared = false;
        if (growLoop != null) StopCoroutine(growLoop);
        growLoop = StartCoroutine(GrowLoop());
    }

    public void BeginDisappear()
    {
        if (CurrentPhase == Phase.Disappearing) return;
        CurrentPhase = Phase.Disappearing;
        if (growLoop != null) { StopCoroutine(growLoop); growLoop = null; }
        StartCoroutine(DisappearSequential());
    }

    // —— 生长阶段 ——
    IEnumerator GrowLoop()
    {
        var wait = new WaitForSeconds(spawnInterval);
        while (CurrentPhase == Phase.Growing)
        {
            if (actives.Count < maxActiveTrees && TrySpawnOne(out var a)) actives.Add(a);
            yield return wait;
        }
    }

    bool TrySpawnOne(out Active a)
    {
        a = null;
        for (int tries = 0; tries < 30; tries++)
        {
            Vector3 dir = Random.onUnitSphere;
            if (!GetSurface(dir, out Vector3 pos, out Vector3 normal)) continue;
            if (!IsFarEnough(pos)) continue;

            var g = pool.GetRandom();
            // 这里插入 ↓↓↓
            g.gameObject.isStatic = false; // 防止静态合批烙印（重要）
            g.transform.SetParent(earth, worldPositionStays: false);
            foreach (var r in g.GetComponentsInChildren<Renderer>(true)) r.enabled = true; // 保证启用

            g.transform.position = pos;
            Quaternion align = Quaternion.FromToRotation(Vector3.up, normal);
            Quaternion yaw = Quaternion.AngleAxis(Random.Range(0f, 360f), normal);
            g.transform.rotation = yaw * align;

            float s = sizeMultiplier * Random.Range(randomScale.x, randomScale.y);
            g.SetSizeMultiplier(s);
            g.Appear();                          // 慢慢长出来

            a = new Active { g = g, pos = pos, normal = normal };
            return true;
        }
        return false;
    }

    // —— 消失阶段 ——
    IEnumerator DisappearSequential()
    {
        // 用快照锁定要处理的对象，遍历过程中 actives 可安全被修改
        var snapshot = actives.ToArray();
        int remaining = snapshot.Length;

        if (remaining == 0) { FinishClear(); yield break; }

        void onHidden(TreeGrower tg)
        {
            tg.OnFullyHidden -= onHidden;         // 取消订阅，避免池化后残留订阅
            tg.ReturnToPool();

            // 从活动列表移除对应项
            actives.RemoveAll(a => a.g == tg);

            if (--remaining == 0)
                FinishClear();
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            var g = snapshot[i].g;

            // 先订阅，再触发，避免极短动画/立即完成丢事件
            g.OnFullyHidden += onHidden;

            // 若这棵树已经是隐藏状态（极端情况下），直接走回调路径
            // 如果你的 TreeGrower 没有 IsFullyHidden，可换成：if (g.transform.localScale == Vector3.zero)
            if (g.IsFullyHidden)
            {
                onHidden(g);
            }
            else
            {
                g.Disappear();
            }

            if (disappearInterval > 0f && i < snapshot.Length - 1)
                yield return new WaitForSeconds(disappearInterval);
        }

        // 这里不再直接 FinishClear；等待 onHidden 把 remaining 减到 0 再收尾
    }


    void FinishClear()
    {
        CurrentPhase = Phase.Idle;
        AllCleared = true;
        OnAllCleared?.Invoke();
    }

    public void ForceClearNow()
    {
        if (growLoop != null) { StopCoroutine(growLoop); growLoop = null; }
        CurrentPhase = Phase.Idle;

        // 真·杀光：销毁 Earth 下所有 TreeGrower
        var trees = earth.GetComponentsInChildren<TreeGrower>(true);
        foreach (var t in trees)
        {
            if (Application.isPlaying) Destroy(t.gameObject);
            else DestroyImmediate(t.gameObject);
        }
        actives.Clear();
        AllCleared = true;
        OnAllCleared?.Invoke();
    }

    // —— 球面工具 ——（与之前一致）
    bool GetSurface(Vector3 dir, out Vector3 pos, out Vector3 normal)
    {
        if (earthCollider)
        {
            Vector3 start = earth.position + dir * (GetRadius() * 2f);
            if (Physics.Raycast(start, -dir, out RaycastHit hit, GetRadius() * 3f, ~0, QueryTriggerInteraction.Collide)
                && (hit.collider == earthCollider || hit.collider.transform.IsChildOf(earth)))
            {
                pos = hit.point + hit.normal * surfaceOffset;
                normal = hit.normal;
                return true;
            }
        }
        pos = earth.position + dir * (GetRadius() + surfaceOffset);
        normal = dir; return true;
    }
    float GetRadius()
    {
        if (earthCollider is SphereCollider sc)
        {
            float s = Mathf.Max(earth.lossyScale.x, earth.lossyScale.y, earth.lossyScale.z);
            return sc.radius * s;
        }
        return sphereRadius;
    }
    bool IsFarEnough(Vector3 candidate)
    {
        if (actives.Count == 0) return true;
        float R = GetRadius();
        Vector3 cDir = (candidate - earth.position).normalized;
        foreach (var a in actives)
        {
            float angle = Vector3.Angle((a.pos - earth.position).normalized, cDir) * Mathf.Deg2Rad;
            if (angle * R < minSurfaceSpacing) return false;
        }
        return true;
    }
}
