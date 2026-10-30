using UnityEngine;

namespace StageSettAR.Helpers
{
    // Chain several of these (each following the one before it) to get a snake/conga line.
    [AddComponentMenu("StageSettAR/Helpers/Follow Target")]
    public class FollowTarget : MonoBehaviour
    {
        [Tooltip("The object to follow. Use the Main Camera to follow the player.")]
        public Transform target;

        [Tooltip("How fast to move, in meters per second.")]
        public float speed = 1.5f;

        [Tooltip("How close to get before stopping, in meters.")]
        public float followDistance = 1f;

        [Tooltip("Turn to face the target while following.")]
        public bool faceTarget = true;

        [Tooltip("Stay at the starting height (good for a pet or companion following the player's head).")]
        public bool stayOnGround = true;

        [Tooltip("If unchecked, the object waits for StartFollowing() before moving.")]
        public bool followOnStart = true;

        private bool isFollowing;
        private float groundHeight;

        private void Start()
        {
            isFollowing = followOnStart;
            groundHeight = transform.position.y;
        }

        private void Update()
        {
            if (!isFollowing || target == null)
            {
                return;
            }

            Vector3 targetPosition = target.position;
            if (stayOnGround)
            {
                targetPosition.y = groundHeight;
            }

            Vector3 toTarget = targetPosition - transform.position;
            float distance = toTarget.magnitude;
            if (distance < 0.0001f)
            {
                return;
            }

            if (distance > followDistance)
            {
                // Aim for the spot followDistance behind the target, so we never overlap it.
                Vector3 goal = targetPosition - toTarget / distance * followDistance;
                transform.position = Vector3.MoveTowards(transform.position, goal, speed * Time.deltaTime);
            }

            if (faceTarget)
            {
                Quaternion look = Quaternion.LookRotation(toTarget);
                transform.rotation = Quaternion.Slerp(transform.rotation, look, 5f * Time.deltaTime);
            }
        }

        public void StartFollowing()
        {
            isFollowing = true;
        }

        public void StopFollowing()
        {
            isFollowing = false;
        }

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
        }
    }
}
