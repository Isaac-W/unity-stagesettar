using UnityEngine;

namespace StageSettAR.Helpers
{
    [AddComponentMenu("StageSettAR/Helpers/Floater")]
    public class Floater : MonoBehaviour
    {
        [Tooltip("How far up and down to bob, in meters.")]
        public float height = 0.05f;

        [Tooltip("How many bobs per second.")]
        public float bobsPerSecond = 0.5f;

        [Tooltip("Call StopFloating() from a Grab Interactable's Select Entered event so it doesn't fight the player's hand.")]
        public bool floatOnStart = true;

        private bool isFloating;
        private Vector3 startPosition;
        private float startTime;

        private void OnEnable()
        {
            startPosition = transform.localPosition;
            startTime = Time.time;
            isFloating = floatOnStart;
        }

        private void Update()
        {
            if (isFloating)
            {
                float offset = Mathf.Sin((Time.time - startTime) * bobsPerSecond * 2f * Mathf.PI) * height;
                transform.localPosition = startPosition + Vector3.up * offset;
            }
        }

        public void StartFloating()
        {
            startPosition = transform.localPosition;
            startTime = Time.time;
            isFloating = true;
        }

        public void StopFloating()
        {
            isFloating = false;
        }
    }
}
