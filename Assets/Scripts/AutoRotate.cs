using UnityEngine;

public class AutoRotate : MonoBehaviour
{
    public Vector3 axis = new Vector3(0, 1, 0);   // 围绕哪个轴转
    public float speed = 5f;                      // 每秒度数

    void Update()
    {
        transform.Rotate(axis * speed * Time.deltaTime, Space.Self);
    }
}
