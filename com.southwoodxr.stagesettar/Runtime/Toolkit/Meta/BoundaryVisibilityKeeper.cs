using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.OpenXR.NativeTypes;

namespace StageSettAR
{
    /// <summary>
    /// Keeps Meta's guardian boundary in the state this app wants it, for the whole session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>Boundary Visibility</c> OpenXR feature asks for suppression <b>once</b>, when the
    /// OpenXR instance is created, retrying for about twelve seconds and then giving up. It
    /// never asks again. So anything that takes suppression away later (and the system may
    /// revoke it whenever it likes) leaves the boundary showing for the rest of the run with
    /// nothing to put it back. This asks again.
    /// </para>
    /// <para>
    /// <b>Suppression only holds while passthrough is rendering.</b> The feature's own
    /// validation says so: "Your app can only suppress boundary visibility while rendering
    /// Passthrough." A scene that turns passthrough off cannot suppress the boundary no matter how
    /// often it is asked, so this component is not a substitute for keeping passthrough on. After a
    /// Passthrough Fade goes fully virtual the boundary may come back; this keeps asking, and it
    /// goes away again as soon as passthrough returns.
    /// </para>
    /// <para>
    /// Re-asks on each scene load, and watches every frame for the state drifting. The watch is
    /// free (reading <c>currentVisibility</c> is a property read); only the request itself is rate
    /// limited, because that one crosses into native code. The StageSettAR rig carries one, so
    /// scenes made from the template need nothing.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("StageSettAR/Boundary Visibility Keeper")]
    public sealed class BoundaryVisibilityKeeper : MonoBehaviour
    {
        [Tooltip("The state to hold. Suppressed hides the guardian, which is what a mapped " +
                 "room-scale experience wants; Not Suppressed restores the system default.")]
        [SerializeField]
        XrBoundaryVisibility _desired = XrBoundaryVisibility.VisibilitySuppressed;

        [Tooltip("Seconds between requests while the state is not what was asked for. The " +
                 "state is checked every frame regardless; this only limits how often the " +
                 "native call is made, since it can fail for a while before it takes.")]
        [SerializeField, Min(0.05f)]
        float _retrySeconds = 0.5f;

        [Tooltip("Log every change and the first failure of each kind. Off once it is working.")]
        [SerializeField]
        bool _log = true;

        static BoundaryVisibilityKeeper s_Instance;

        BoundaryVisibilityFeature _feature;
        float _nextRequestAt;
        bool _warnedMissing;
        bool _warnedNotAllowed;

        void Awake()
        {
            // One keeper at a time: a second copy would fight the first over the same native
            // state. Only this component goes, never its GameObject, which is part of the rig.
            if (s_Instance != null && s_Instance != this)
            {
                Destroy(this);
                return;
            }
            s_Instance = this;
        }

        void OnEnable()
        {
            if (s_Instance != this) return;
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (!TryResolveFeature()) return;
            _feature.boundaryVisibilityChanged += OnVisibilityChanged;
            RequestNow("this component started");
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_feature != null) _feature.boundaryVisibilityChanged -= OnVisibilityChanged;
        }

        void OnDestroy()
        {
            if (s_Instance == this) s_Instance = null;
        }

        /// <summary>
        /// Ask again as each scene comes up, rather than waiting for the retry interval.
        /// </summary>
        /// <remarks>
        /// A scene load is the likeliest moment for the state to have been lost: passthrough
        /// goes down and up with the rig, and suppression cannot survive passthrough being off.
        /// </remarks>
        void OnSceneLoaded(Scene scene, LoadSceneMode mode) => RequestNow($"'{scene.name}' loaded");

        void Update()
        {
            if (s_Instance != this) return;
            if (_feature == null)
            {
                if (!TryResolveFeature()) return;
                _feature.boundaryVisibilityChanged += OnVisibilityChanged;
            }
            if (_feature.currentVisibility == _desired) return;
            if (Time.unscaledTime < _nextRequestAt) return;
            Request();
        }

        void RequestNow(string why)
        {
            _nextRequestAt = 0f;
            if (_feature == null) return;
            if (_feature.currentVisibility == _desired) return;
            if (_log) Debug.Log($"[Boundary] asking for {_desired} because {why}.", this);
            Request();
        }

        void Request()
        {
            _nextRequestAt = Time.unscaledTime + Mathf.Max(0.05f, _retrySeconds);

            XrResult result = _feature.TryRequestBoundaryVisibility(_desired);
            if (result.IsUnqualifiedSuccess()) return;

            // Reported once rather than every interval. The common causes are temporary and
            // fix themselves (the session is not up yet, or passthrough is not rendering),
            // and a line every half second while someone walks around is worse than silence.
            if ((int)result == BoundaryVisibilityFeature.XR_BOUNDARY_VISIBILITY_SUPPRESSION_NOT_ALLOWED_META)
            {
                if (_warnedNotAllowed || !_log) return;
                _warnedNotAllowed = true;
                Debug.LogWarning(
                    "[Boundary] the runtime will not suppress the boundary right now. It only " +
                    "allows it while passthrough is rendering, so check that nothing in this " +
                    "scene has turned passthrough off (a Passthrough Fade that has gone fully " +
                    "virtual does). Retrying quietly.", this);
            }
        }

        void OnVisibilityChanged(object sender, XrBoundaryVisibility visibility)
        {
            if (_log)
            {
                Debug.Log($"[Boundary] the runtime changed boundary visibility to {visibility}" +
                          $"{(visibility == _desired ? "." : $", which is not the {_desired} asked for; asking again.")}",
                          this);
            }
            if (visibility != _desired) _nextRequestAt = 0f;
        }

        bool TryResolveFeature()
        {
            OpenXRSettings settings = OpenXRSettings.Instance;
            _feature = settings != null ? settings.GetFeature<BoundaryVisibilityFeature>() : null;

            if (_feature != null && _feature.enabled) return true;

            // Not fatal and not this component's business to fix: a build without the feature,
            // or a platform that is not Meta, simply has no boundary to suppress.
            if (!_warnedMissing)
            {
                _warnedMissing = true;
                if (_log)
                {
                    Debug.LogWarning(
                        "[Boundary] the Meta Quest boundary visibility feature is not enabled, " +
                        "so the boundary cannot be controlled. Enable it under Project " +
                        "Settings > XR Plug-in Management > OpenXR for this build target.",
                        this);
                }
            }
            _feature = null;
            return false;
        }
    }
}
