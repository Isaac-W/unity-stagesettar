using UnityEditor;
using UnityEngine;

namespace StageSettAR.Editor
{
    [FilePath("ProjectSettings/StageSettARSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    sealed class StageSettARSettings : ScriptableSingleton<StageSettARSettings>
    {
        [SerializeField] bool m_ignoreSplatsInVcs = true;

        /// <summary>Keep the downloaded UnitySplats copy out of version control.</summary>
        public bool IgnoreSplatsInVcs
        {
            get => m_ignoreSplatsInVcs;
            set
            {
                if (m_ignoreSplatsInVcs == value) return;
                m_ignoreSplatsInVcs = value;
                Save(true);
                SplatVcsIgnore.Apply();
            }
        }
    }
}
