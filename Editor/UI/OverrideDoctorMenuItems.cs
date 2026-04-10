using UnityEditor;
using UnityEngine;

namespace SashaRX.OverrideDoctor
{
    /// <summary>
    /// Adds context menu items to Hierarchy and Project windows
    /// for quick access to Override Doctor.
    /// </summary>
    public static class OverrideDoctorMenuItems
    {
        [MenuItem("GameObject/Override Doctor/Analyze This Prefab", false, 49)]
        private static void AnalyzeFromHierarchy()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null)
            {
                Debug.LogWarning("[Override Doctor] Selected object is not a prefab instance.");
                return;
            }

            var window = EditorWindow.GetWindow<OverrideDoctorWindow>("Override Doctor");
            window.SetTargetAndAnalyze(root);
        }

        [MenuItem("GameObject/Override Doctor/Analyze This Prefab", true)]
        private static bool AnalyzeFromHierarchyValidate()
        {
            return Selection.activeGameObject != null &&
                   PrefabUtility.IsPartOfPrefabInstance(Selection.activeGameObject);
        }

        [MenuItem("GameObject/Override Doctor/Analyze Subtree From Here", false, 50)]
        private static void AnalyzeSubtreeFromHierarchy()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null) return;

            var window = EditorWindow.GetWindow<OverrideDoctorWindow>("Override Doctor");
            window.SetTargetAndAnalyze(root, go.transform);
        }

        [MenuItem("GameObject/Override Doctor/Analyze Subtree From Here", true)]
        private static bool AnalyzeSubtreeValidate()
        {
            return Selection.activeGameObject != null &&
                   PrefabUtility.IsPartOfPrefabInstance(Selection.activeGameObject);
        }
    }
}
