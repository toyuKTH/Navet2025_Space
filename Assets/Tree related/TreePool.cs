using System.Collections.Generic;
using UnityEngine;

public class TreePool : MonoBehaviour
{
    public GameObject[] prefabs;      // 把4个树预制体都拖进来
    public int prewarmEach = 10;

    List<Queue<TreeGrower>> queues = new List<Queue<TreeGrower>>();

    void Awake()
    {
        Prewarm();
    }

    public void Prewarm()
    {
        queues.Clear();
        for (int i = 0; i < prefabs.Length; i++)
        {
            var q = new Queue<TreeGrower>();
            queues.Add(q);
            for (int k = 0; k < prewarmEach; k++)
            {
                var t = Create(i);
                t.gameObject.SetActive(false);
                q.Enqueue(t);
            }
        }
    }

    TreeGrower Create(int index)
    {
        var go = Instantiate(prefabs[index], transform);
        var g = go.GetComponent<TreeGrower>() ?? go.AddComponent<TreeGrower>();
        g.pool = this;
        g.poolIndex = index;
        return g;
    }

    public TreeGrower Get(int index)
    {
        if (index < 0 || index >= prefabs.Length) index = 0;
        var q = queues[index];
        var g = q.Count > 0 ? q.Dequeue() : Create(index);
        g.gameObject.SetActive(true);
        return g;
    }
    public TreeGrower GetRandom()
    {
        return Get(Random.Range(0, prefabs.Length));
    }

    public void Return(TreeGrower t)
    {
        if (!t) return;
        t.transform.SetParent(transform, worldPositionStays: false);
        // 保险：回收前先关渲染器，避免一帧残影
        var renderers = t.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = false;

        t.gameObject.SetActive(false);
        queues[t.poolIndex].Enqueue(t);
    }
}
