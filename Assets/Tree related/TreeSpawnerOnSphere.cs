using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using Random = UnityEngine.Random;


public class TreeSpawnerOnSphere : MonoBehaviour
{
    [Header("����")]
    public Transform earth;
    public Collider earthCollider;      // SphereCollider/MeshCollider
    public TreePool pool;

    [Header("����")]
    public float spawnInterval = 0.5f;      // �������ࣨԽ��Խ����
    public float disappearInterval = 0.2f;  // ��ʧ���ࣨ���������ʧ��0=ͬʱ��
    public float sizeMultiplier = 0.25f;

    [Header("����")]
    public float surfaceOffset = 0.02f;
    public float minSurfaceSpacing = 0.8f;
    public float sphereRadius = 5f;
    public Vector2 randomScale = new Vector2(0.8f, 1.2f);
    public int maxActiveTrees = 80;

    public enum Phase { Idle, Growing, Disappearing }
    public Phase CurrentPhase { get; private set; } = Phase.Idle;


    public bool AllCleared { get; private set; } = true;  // �Ƿ���ȫ����ʧ
    public event Action OnAllCleared;                     // ȫ����ʧ�ص�

    class Active { public TreeGrower g; public Vector3 pos; public Vector3 normal; }
    readonly List<Active> actives = new List<Active>();
    Coroutine growLoop;

    // ���� �ⲿ���� ����
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

    // ���� �����׶� ����
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
            // ������� ������
            g.gameObject.isStatic = false; // ��ֹ��̬������ӡ����Ҫ��
            g.transform.SetParent(earth, worldPositionStays: false);
            foreach (var r in g.GetComponentsInChildren<Renderer>(true)) r.enabled = true; // ��֤����

            g.transform.position = pos;
            Quaternion align = Quaternion.FromToRotation(Vector3.up, normal);
            Quaternion yaw = Quaternion.AngleAxis(Random.Range(0f, 360f), normal);
            g.transform.rotation = yaw * align;

            float s = sizeMultiplier * Random.Range(randomScale.x, randomScale.y);
            g.SetSizeMultiplier(s);
            g.Appear();                          // ����������

            a = new Active { g = g, pos = pos, normal = normal };
            return true;
        }
        return false;
    }

    // ���� ��ʧ�׶� ����
    IEnumerator DisappearSequential()
    {
        // �ÿ�������Ҫ�����Ķ��󣬱��������� actives �ɰ�ȫ���޸�
        var snapshot = actives.ToArray();
        int remaining = snapshot.Length;

        if (remaining == 0) { FinishClear(); yield break; }

        void onHidden(TreeGrower tg)
        {
            tg.OnFullyHidden -= onHidden;         // ȡ�����ģ�����ػ����������
            tg.ReturnToPool();

            // �ӻ�б��Ƴ���Ӧ��
            actives.RemoveAll(a => a.g == tg);

            if (--remaining == 0)
                FinishClear();
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            var g = snapshot[i].g;

            // �ȶ��ģ��ٴ��������⼫�̶���/������ɶ��¼�
            g.OnFullyHidden += onHidden;

            // ��������Ѿ�������״̬����������£���ֱ���߻ص�·��
            // ������ TreeGrower û�� IsFullyHidden���ɻ��ɣ�if (g.transform.localScale == Vector3.zero)
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

        // ���ﲻ��ֱ�� FinishClear���ȴ� onHidden �� remaining ���� 0 ����β
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

        // �桤ɱ�⣺���� Earth ������ TreeGrower
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

    // ���� ���湤�� ��������֮ǰһ�£�
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
