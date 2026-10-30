using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// Makes this object's renderer editor-only while the object itself stays in builds, for an object
    /// that is both a reference picture and something real, like a map's Floor: its floor image is only
    /// for placing content, its collider is what dropped objects land on.
    /// </summary>
    /// <remarks>
    /// The EditorOnly tag cannot do this, because it removes the whole object. Builds strip the renderer
    /// and mesh from scenes (so the image is not even packed); anything that still reaches a player, such
    /// as the prefab spawned at runtime, loses them on Awake.
    /// </remarks>
    [AddComponentMenu("StageSettAR/Editor Only Visual")]
    [DisallowMultipleComponent]
    public sealed class EditorOnlyVisual : MonoBehaviour
    {
        void Awake()
        {
            if (Application.isEditor) return;
            Strip(gameObject);
        }

        /// <summary>Removes the renderer and mesh filter from the object, and this component with them.</summary>
        public static void Strip(GameObject go)
        {
            if (go.TryGetComponent(out Renderer renderer)) DestroyImmediate(renderer);
            if (go.TryGetComponent(out MeshFilter filter)) DestroyImmediate(filter);
            if (go.TryGetComponent(out EditorOnlyVisual marker)) DestroyImmediate(marker);
        }
    }
}
