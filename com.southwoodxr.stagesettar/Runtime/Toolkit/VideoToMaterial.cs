using UnityEngine;
using UnityEngine.Video;

namespace StageSettAR
{
    /// <summary>
    /// "Put this video on this renderer", without a RenderTexture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Put it on the object with the VideoPlayer and the quad, or point it at them. The Add Video
    /// menu (Tools &gt; StageSettAR &gt; Add Video) builds all of this for you.
    /// </para>
    /// <para>
    /// Why this exists: on Android with Vulkan, which is what a Quest build uses, a VideoPlayer
    /// writing into a RenderTexture commonly decodes fine but never presents a picture: the audio
    /// plays and the surface stays blank. API Only sidesteps that by handing over the decoder's own
    /// texture, which this then binds straight onto the material.
    /// </para>
    /// <para>
    /// It also handles the two things that make a hand-rolled version fail: the texture does not
    /// exist until the player has prepared, and it can be replaced afterwards, on loop, on a
    /// resolution change, or when the app returns from the background.
    /// </para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/Video To Material")]
    [DisallowMultipleComponent]
    public sealed class VideoToMaterial : MonoBehaviour
    {
        [Tooltip("The video to show. Found automatically on this object when empty.")]
        public VideoPlayer videoPlayer;

        [Tooltip("The renderer to show it on: the quad's Mesh Renderer. Found automatically on this object when empty.")]
        public Renderer targetRenderer;

        [Tooltip("Texture property to write to. URP shaders use _BaseMap; the old built-in ones " +
                 "use _MainTex. This is the usual reason a video plays with no picture: the " +
                 "default _MainTex does not exist on a URP/Unlit material.")]
        public string textureProperty = "_BaseMap";

        [Tooltip("Also write to _MainTex, for a material that uses the older name. Harmless when " +
                 "the property is absent.")]
        public bool alsoSetMainTex = true;

        [Tooltip("Show black until the first frame arrives. The material keeps its own texture " +
                 "until then, so without this a quad can flash whatever it was authored with.")]
        public bool blackUntilReady = true;

        Material _instance;
        Texture _bound;
        int _propertyId;
        int _mainTexId;
        bool _warned;

        void Reset()
        {
            videoPlayer = GetComponent<VideoPlayer>();
            targetRenderer = GetComponent<Renderer>();
        }

        void OnEnable()
        {
            _propertyId = Shader.PropertyToID(string.IsNullOrEmpty(textureProperty) ? "_BaseMap" : textureProperty);
            _mainTexId = Shader.PropertyToID("_MainTex");
            _bound = null;

            if (videoPlayer == null) videoPlayer = GetComponent<VideoPlayer>();
            if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();

            if (videoPlayer == null || targetRenderer == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning($"[VideoToMaterial] {name}: needs both a VideoPlayer and a " +
                                     "Renderer. Assign them, or put this on the object that has " +
                                     "them.", this);
                }
                enabled = false;
                return;
            }

            // The whole point: no RenderTexture in the path.
            videoPlayer.renderMode = VideoRenderMode.APIOnly;

            // material, not sharedMaterial: sharedMaterial would write the video texture into
            // the asset itself and dirty it for every other object using it.
            if (_instance == null) _instance = targetRenderer.material;

            if (blackUntilReady) Bind(Texture2D.blackTexture);
        }

        void Update()
        {
            if (videoPlayer == null || _instance == null) return;

            // Re-checked every frame rather than hooked to prepareCompleted: the decoder can
            // swap the texture out later, and a one-shot assignment would leave the material
            // pointing at a texture that no longer receives frames.
            Texture texture = videoPlayer.texture;
            if (texture == null || texture == _bound) return;
            Bind(texture);
        }

        void Bind(Texture texture)
        {
            _bound = texture;
            _instance.SetTexture(_propertyId, texture);
            if (alsoSetMainTex) _instance.SetTexture(_mainTexId, texture);
        }

        void OnDestroy()
        {
            // targetRenderer.material made a copy; it is ours to clean up.
            if (_instance != null) Destroy(_instance);
        }
    }
}
