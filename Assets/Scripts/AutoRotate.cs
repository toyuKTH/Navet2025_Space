using UnityEngine;

public class AutoRotate : MonoBehaviour
{
    public Vector3 axis = new Vector3(0, 1, 0);   // 围绕哪个轴转
    public float speed = 5f;                      // 每秒度数
    [Header("同步选项")]
    public bool useGlobalClock = true;            // 基于 Time.time 的绝对旋转，跨面板同步
    public float phaseOffsetDeg = 0f;             // 相位偏移（度），用于手动对齐不同预制的初始相位

    Quaternion _initialLocalRotation;
    Vector3 _axisNorm;

    void Awake()
    {
        _initialLocalRotation = transform.localRotation;
        _axisNorm = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;
    }

    void Update()
    {
        if (!useGlobalClock)
        {
            transform.Rotate(_axisNorm * speed * Time.deltaTime, Space.Self);
            return;
        }

        // 基于全局时间的确定性角度：不同面板激活时刻不同也不会跳变
        float angle = (Time.time * speed + phaseOffsetDeg) % 360f;
        transform.localRotation = Quaternion.AngleAxis(angle, _axisNorm) * _initialLocalRotation;
    }
}
