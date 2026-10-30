using UnityEngine;

namespace StageSettAR.Helpers
{
    // Keeps text panels and signs readable by turning them toward the headset.
    [AddComponentMenu("StageSettAR/Helpers/Face Player")]
    public class FacePlayer : MonoBehaviour
    {
        [Tooltip("Only turn left/right (stays upright). Uncheck to also tilt up/down.")]
        public bool uprightOnly = true;

        [Tooltip("Check this for TextMeshPro text, which reads backwards otherwise.")]
        public bool flip = true;

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            Vector3 toCamera = cam.transform.position - transform.position;
            if (uprightOnly)
            {
                toCamera.y = 0f;
            }
            if (toCamera.sqrMagnitude < 0.0001f)
            {
                return;
            }

            transform.rotation = Quaternion.LookRotation(flip ? -toCamera : toCamera);
        }
    }
}
