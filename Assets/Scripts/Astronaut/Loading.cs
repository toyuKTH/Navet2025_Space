using UnityEngine;

namespace AstronautPlayer
{
    public class AstronautAutoWalker : MonoBehaviour
    {
        private Animator anim;
        private CharacterController controller;

        [Header("移动参数")]
        public float moveDistance = 5f;
        public float moveSpeed = 2f;
        public float gravity = 20.0f;

        private Vector3 startPos;
        private Vector3 moveDirection = Vector3.zero;
        private bool isWalking = false;

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            anim = gameObject.GetComponentInChildren<Animator>();
        }

        void Update()
        {
            if (!isWalking) return;

            Vector3 targetPos = startPos + transform.forward * moveDistance;
            Vector3 dir = (targetPos - transform.position).normalized;
            moveDirection = dir * moveSpeed;

            if (!controller.isGrounded)
                moveDirection.y -= gravity * Time.deltaTime;

            controller.Move(moveDirection * Time.deltaTime);

            anim.SetInteger("AnimationPar", 1);

            if (Vector3.Distance(transform.position, targetPos) < 0.1f)
            {
                StopWalking();
            }
        }

        public void StartWalking()
        {
            startPos = transform.position; // 每次从当前位置开始
            isWalking = true;
            anim.SetInteger("AnimationPar", 1);
            Debug.Log("👨‍🚀 宇航员开始行走");
        }

        private void StopWalking()
        {
            isWalking = false;
            anim.SetInteger("AnimationPar", 0);
            Debug.Log("👨‍🚀 宇航员到达终点，停止行走");
        }
    }
}
