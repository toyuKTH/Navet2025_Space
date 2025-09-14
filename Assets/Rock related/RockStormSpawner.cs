using System.Collections;
using UnityEngine;

public class RockStormSpawner : MonoBehaviour
{
    [Header("引用")]
    public Transform sourcePlanet;   // 中间星球
    public Transform targetEarth;    // 地球
    public RockPool pool;
    public RockStormConfig config;

    [Header("几何")]
    public float sourceRadius = 2.5f; // 中间星球半径（估个值或用模型bounds算）

    Coroutine loop;

    public void Activate(bool on)
    {
        if (on && loop == null)
        {
            pool.Prewarm(config.prewarm);
            loop = StartCoroutine(SpawnLoop());
        }
        else if (!on && loop != null)
        {
            StopCoroutine(loop); loop = null;
        }
    }

    IEnumerator SpawnLoop()
    {
        var wait = new WaitForSeconds(1f / Mathf.Max(0.0001f, config.spawnRate));
        while (true) { SpawnOne(); yield return wait; }
    }

    void SpawnOne()
    {
        // 朝向地球的半球
        Vector3 dirToTarget = (targetEarth.position - sourcePlanet.position).normalized;
        Vector3 rand = Random.onUnitSphere;
        if (Vector3.Dot(rand, dirToTarget) < 0f) rand = -rand;

        float shell = config.spawnShell * Random.value;
        Vector3 spawnPos = sourcePlanet.position + rand * (sourceRadius + shell);

        var rock = pool.Get();
        rock.transform.position = spawnPos;

        Vector3 toTarget = (targetEarth.position - spawnPos).normalized;
        Vector3 startDir = (toTarget + Random.insideUnitSphere * config.spread).normalized;
        float speed = Random.Range(config.initialSpeed.x, config.initialSpeed.y);
        Vector3 startVel = startDir * speed;

        rock.onDone = (r) => pool.Return(r);
        rock.Launch(targetEarth, startVel, config.homingAccel, config.maxLifetime);
    }
}
