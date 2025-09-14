using System;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class RockMover : MonoBehaviour
{
    Rigidbody rb;
    Transform target;
    float homingAccel, maxLifetime, life;
    public Action<RockMover> onDone;

    void Awake() { rb = GetComponent<Rigidbody>(); }

    public void Launch(Transform target, Vector3 startVel, float homingAccel, float maxLifetime)
    {
        this.target = target;
        this.homingAccel = homingAccel;
        this.maxLifetime = maxLifetime;
        life = 0f;
        gameObject.SetActive(true);
        rb.velocity = startVel;
        rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 2f; // 随机自转
    }

    void FixedUpdate()
    {
        if (!target)
        {
            // 只提示一次即可，避免刷屏
            if (enabled) { Debug.LogWarning("RockMover: target 未设置。"); enabled = false; }
            return;
        }
        Debug.DrawLine(rb.position, target.position);
        if (!target) return;
        Vector3 toTarget = (target.position - rb.position).normalized;
        rb.AddForce(toTarget * homingAccel, ForceMode.Acceleration);
        if (rb.velocity.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(rb.velocity);
        life += Time.fixedDeltaTime;
        if (life > maxLifetime) onDone?.Invoke(this);
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Earth")) onDone?.Invoke(this);
    }
}
