using UnityEngine;

public class CameraController : MonoBehaviour
{
    public Transform target;         // 目标物体，比如星球
    public float zoomSpeed = 5f;
    public float rotateSpeed = 50f;
    public float minDistance = 2f;
    public float maxDistance = 50f;

    private float currentDistance;
    private Vector3 direction;

    void Start()
    {
        if (target == null)
        {
            Debug.LogWarning("CameraController: 请在 Inspector 中设置目标 target");
            return;
        }

        direction = (transform.position - target.position).normalized;
        currentDistance = Vector3.Distance(transform.position, target.position);
    }

    void Update()
    {
        if (target == null) return;

        // 缩放
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            currentDistance -= zoomSpeed;
        }
        if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            currentDistance += zoomSpeed;
        }

        // 限制距离范围
        currentDistance = Mathf.Clamp(currentDistance, minDistance, maxDistance);
        transform.position = target.position + direction * currentDistance;

        // 旋转
        if (Input.GetKey(KeyCode.Alpha3))
        {
            transform.RotateAround(target.position, Vector3.up, rotateSpeed * Time.deltaTime);
            direction = (transform.position - target.position).normalized;
        }
        if (Input.GetKey(KeyCode.Alpha4))
        {
            transform.RotateAround(target.position, Vector3.up, -rotateSpeed * Time.deltaTime);
            direction = (transform.position - target.position).normalized;
        }

        // 始终看向目标
        transform.LookAt(target);
    }
}
