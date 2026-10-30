using UnityEngine;

namespace StageSettAR.Helpers
{
    // Not called ObjectSpawner because XR Interaction Toolkit samples already have one.
    [AddComponentMenu("StageSettAR/Helpers/Simple Spawner")]
    public class SimpleSpawner : MonoBehaviour
    {
        [Tooltip("The prefab to copy. Drag it in from the Project window, not the Hierarchy.")]
        public GameObject prefab;

        [Tooltip("Where copies appear. Leave empty to use this object's position.")]
        public Transform spawnPoint;

        [Tooltip("Stop spawning after this many copies. 0 = no limit.")]
        public int maxCount = 5;

        private int spawnedCount;

        public void Spawn()
        {
            if (prefab == null || (maxCount > 0 && spawnedCount >= maxCount))
            {
                return;
            }
            Transform point = spawnPoint != null ? spawnPoint : transform;
            Instantiate(prefab, point.position, point.rotation);
            spawnedCount++;
        }
    }
}
