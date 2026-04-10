using UnityEngine;

namespace SashaRX.OverrideDoctor
{
    /// <summary>
    /// Persistent settings for Override Doctor.
    /// Create via Assets → Create → Override Doctor → Settings,
    /// or the tool will use defaults.
    /// </summary>
    [CreateAssetMenu(
        fileName = "OverrideDoctorSettings",
        menuName = "Override Doctor/Settings")]
    public class OverrideDoctorSettings : ScriptableObject
    {
        [Header("Float Comparison")]
        [Tooltip("Absolute epsilon for position/scale properties")]
        public float PositionEpsilon = 1e-5f;

        [Tooltip("Absolute epsilon for generic float properties")]
        public float GenericFloatEpsilon = 1e-4f;

        [Tooltip("Relative epsilon (fraction of value magnitude)")]
        public float RelativeEpsilon = 1e-5f;

        [Header("Rotation Comparison")]
        [Tooltip("Angle threshold in degrees for quaternion comparison")]
        public float QuaternionAngleThreshold = 0.01f;

        [Tooltip("Angle threshold in degrees for Euler hint comparison")]
        public float EulerAngleThreshold = 0.01f;

        [Header("Analysis")]
        [Tooltip("Use incremental analysis (non-blocking) for large prefabs")]
        public bool UseIncrementalAnalysis = true;

        [Tooltip("Number of property modifications to process per editor frame")]
        public int IncrementalBatchSize = 300;

        [Header("Ignored Property Paths")]
        [Tooltip("Additional property path prefixes to ignore (one per line)")]
        public string[] AdditionalIgnoredPrefixes = new string[0];

        [Header("Ignored Component Types")]
        [Tooltip("Component type names to skip during analysis")]
        public string[] IgnoredComponentTypes = new string[0];

        // ── Singleton access ───────────────────────────────────────

        private static OverrideDoctorSettings s_Instance;

        public static OverrideDoctorSettings GetOrCreateDefault()
        {
            if (s_Instance != null) return s_Instance;

#if UNITY_EDITOR
            // Try to find existing asset
            var guids = UnityEditor.AssetDatabase.FindAssets("t:OverrideDoctorSettings");
            if (guids.Length > 0)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                s_Instance = UnityEditor.AssetDatabase.LoadAssetAtPath<OverrideDoctorSettings>(path);
                if (s_Instance != null) return s_Instance;
            }
#endif

            // Return runtime defaults (not saved)
            s_Instance = CreateInstance<OverrideDoctorSettings>();
            return s_Instance;
        }
    }
}
