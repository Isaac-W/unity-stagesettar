using UnityEngine;

namespace StageSettAR.Helpers
{
    [AddComponentMenu("StageSettAR/Helpers/Rotator")]
    public class Rotator : MonoBehaviour
    {
        [Tooltip("Which way to spin. (0, 1, 0) spins like a record player; (1, 0, 0) tumbles forward.")]
        public Vector3 axis = Vector3.up;

        [Tooltip("How fast to spin, in degrees per second. Negative numbers spin the other way.")]
        public float degreesPerSecond = 45f;

        [Tooltip("Self = spin around the object's own axes. World = spin around the scene's axes.")]
        public Space space = Space.Self;

        [Tooltip("If unchecked, the object waits for StartRotating() before spinning.")]
        public bool rotateOnStart = true;

        private bool isRotating;

        private void Start()
        {
            isRotating = rotateOnStart;
        }

        private void Update()
        {
            if (isRotating)
            {
                transform.Rotate(axis.normalized, degreesPerSecond * Time.deltaTime, space);
            }
        }

        public void StartRotating()
        {
            isRotating = true;
        }

        public void StopRotating()
        {
            isRotating = false;
        }

        public void SetSpeed(float newDegreesPerSecond)
        {
            degreesPerSecond = newDegreesPerSecond;
        }
    }
}
