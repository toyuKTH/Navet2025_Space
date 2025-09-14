using UnityEngine;

[CreateAssetMenu(menuName = "Game/RockStormConfig")]
public class RockStormConfig : ScriptableObject
{
    [Header("发射")]
    public float spawnRate = 6f;           // 每秒发射多少个
    public float spawnShell = 1.0f;        // 从星球表面往外偏移的随机壳厚
    public Vector2 initialSpeed = new Vector2(8f, 12f);
    public float spread = 0.6f;            // 初始方向的随机散射

    [Header("飞行")]
    public float homingAccel = 5f;         // 向地球的“吸引”加速度
    public float maxLifetime = 10f;        // 最长存活

    [Header("性能")]
    public int prewarm = 20;               // 预热池大小
}
