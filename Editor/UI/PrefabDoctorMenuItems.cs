using UnityEditor;
using UnityEngine;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Adds context menu items to Hierarchy and Project windows
    /// for quick access to Prefab Doctor.
    /// </summary>
    public static class PrefabDoctorMenuItems
    {
        [MenuItem("GameObject/Prefab Doctor/Analyze This Prefab", false, 49)]
        private static void AnalyzeFromHierarchy()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null)
            {
                Debug.LogWarning("[Prefab Doctor] Selected object is not a prefab instance.");
                return;
            }

            var window = EditorWindow.GetWindow<PrefabDoctorWindow>("Prefab Doctor");
            window.SetTargetAndAnalyze(root);
        }

        [MenuItem("GameObject/Prefab Doctor/Analyze This Prefab", true)]
        private static bool AnalyzeFromHierarchyValidate()
        {
            return Selection.activeGameObject != null &&
                   PrefabUtility.IsPartOfPrefabInstance(Selection.activeGameObject);
        }

        [MenuItem("GameObject/Prefab Doctor/Analyze Subtree From Here", false, 50)]
        private static void AnalyzeSubtreeFromHierarchy()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null) return;

            var window = EditorWindow.GetWindow<PrefabDoctorWindow>("Prefab Doctor");
            window.SetTargetAndAnalyze(root, go.transform);
        }

        [MenuItem("GameObject/Prefab Doctor/Analyze Subtree From Here", true)]
        private static bool AnalyzeSubtreeValidate()
        {
            return Selection.activeGameObject != null &&
                   PrefabUtility.IsPartOfPrefabInstance(Selection.activeGameObject);
        }

        [MenuItem("GameObject/Prefab Doctor/Analyze Full Hierarchy", false, 51)]
        private static void AnalyzeHierarchyFromMenu()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var window = EditorWindow.GetWindow<PrefabDoctorWindow>("Prefab Doctor");
            window.SetTargetAndAnalyzeHierarchy(go);
        }

        [MenuItem("GameObject/Prefab Doctor/Analyze Full Hierarchy", true)]
        private static bool AnalyzeHierarchyValidate()
        {
            return Selection.activeGameObject != null;
        }
    }
}
