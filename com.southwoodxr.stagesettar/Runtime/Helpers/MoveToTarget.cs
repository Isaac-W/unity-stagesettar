using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR.Helpers
{
    [AddComponentMenu("StageSettAR/Helpers/Move To Target")]
    public class MoveToTarget : MonoBehaviour
    {
        [Tooltip("Where to go. Tip: make an empty GameObject as a marker.")]
        public Transform destination;

        [Tooltip("How fast to move, in meters per second.")]
        public float speed = 1f;

        [Tooltip("Turn to face the direction of travel.")]
        public bool faceDirection = true;

        [Tooltip("If unchecked, the object waits for StartMoving() before moving.")]
        public bool moveOnStart = false;

        [Tooltip("Fired once when the object arrives.")]
        public UnityEvent onArrived = new UnityEvent();

        private bool isMoving;

        private void Start()
        {
            isMoving = moveOnStart;
        }

        private void Update()
        {
            if (!isMoving || destination == null)
            {
                return;
            }

            Vector3 toGoal = destination.position - transform.position;
            Vector3 flat = new Vector3(toGoal.x, 0f, toGoal.z);
            if (faceDirection && flat.sqrMagnitude > 0.0001f)
            {
                Quaternion look = Quaternion.LookRotation(flat);
                transform.rotation = Quaternion.Slerp(transform.rotation, look, 5f * Time.deltaTime);
            }

            transform.position = Vector3.MoveTowards(transform.position, destination.position, speed * Time.deltaTime);
            if (transform.position == destination.position)
            {
                isMoving = false;
                onArrived.Invoke();
            }
        }

        public void StartMoving()
        {
            isMoving = true;
        }

        public void StopMoving()
        {
            isMoving = false;
        }

        public void SetDestination(Transform newDestination)
        {
            destination = newDestination;
        }
    }
}
