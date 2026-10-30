using UnityEngine;

namespace StageSettAR.Helpers
{
    // Handy for testing events before the real sounds/models are hooked up.
    [AddComponentMenu("StageSettAR/Helpers/Debug Message")]
    public class DebugMessage : MonoBehaviour
    {
        public void Log(string message)
        {
            Debug.Log($"[{gameObject.name}] {message}", this);
        }
    }
}
