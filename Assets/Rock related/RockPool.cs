using System.Collections.Generic;
using UnityEngine;

public class RockPool : MonoBehaviour
{
    public GameObject rockPrefab;
    readonly Queue<RockMover> q = new Queue<RockMover>();

    RockMover CreateOne()
    {
        if (rockPrefab == null)
        {
            Debug.LogError("RockPool: rockPrefab 未设置。");
            return null;
        }
        var go = Instantiate(rockPrefab, transform);
        var mover = go.GetComponent<RockMover>();
        if (mover == null) mover = go.AddComponent<RockMover>(); // 自动补齐
        return mover;
    }

    public RockMover Get()
    {
        RockMover r = q.Count > 0 ? q.Dequeue() : CreateOne();
        if (r == null) return null;
        r.gameObject.SetActive(true);
        return r;
    }

    public void Return(RockMover r)
    {
        if (r == null)
        {
            Debug.LogWarning("RockPool.Return 收到 null。");
            return;
        }
        r.gameObject.SetActive(false);
        q.Enqueue(r);
    }

    public void Prewarm(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var r = CreateOne();
            if (r == null) break;
            Return(r);
        }
    }
}
