using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SashaRX.OverrideDoctor
{
    /// <summary>
    /// Actions for resolving issues found by ProjectScanner.
    /// All operations support Undo and use AssetDatabase batching.
    /// </summary>
    internal static class ProjectScanActions
    {
        // ── Create Prefab Wrapper for FBX ──────────────────────────

        /// <summary>
        /// Create a Prefab Variant (wrapper) based on a Model Prefab (FBX).
        /// Saves the wrapper next to the FBX with "_Prefab" suffix.
        /// Returns the path to the created wrapper.
        /// </summary>
        public static string CreateFbxWrapper(string fbxPath, string outputFolder = null)
        {
            var modelAsset = AssetDatabase.LoadMainAssetAtPath(fbxPath) as GameObject;
            if (modelAsset == null)
            {
                Debug.LogError($"[Override Doctor] Cannot load model at {fbxPath}");
                return null;
            }

            // Determine output path
            if (outputFolder == null)
                outputFolder = Path.GetDirectoryName(fbxPath);

            string baseName = Path.GetFileNameWithoutExtension(fbxPath);
            string wrapperPath = Path.Combine(outputFolder, baseName + "_Prefab.prefab");
            wrapperPath = AssetDatabase.GenerateUniqueAssetPath(wrapperPath);

            // Instantiate the model, save as prefab variant
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
            try
            {
                var savedPrefab = PrefabUtility.SaveAsPrefabAsset(instance, wrapperPath);
                if (savedPrefab != null)
                {
                    Debug.Log($"[Override Doctor] Created wrapper: {wrapperPath} → base: {fbxPath}");
                    return wrapperPath;
                }
                else
                {
                    Debug.LogError($"[Override Doctor] Failed to save wrapper at {wrapperPath}");
                    return null;
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Batch create wrappers for multiple FBX files.
        /// </summary>
        public static List<(string fbxPath, string wrapperPath)> BatchCreateWrappers(
            IEnumerable<string> fbxPaths, string outputFolder = null)
        {
            var results = new List<(string, string)>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var fbx in fbxPaths)
                {
                    string wrapper = CreateFbxWrapper(fbx, outputFolder);
                    if (wrapper != null)
                        results.Add((fbx, wrapper));
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            return results;
        }

        // ── Replace FBX Base with Wrapper ──────────────────────────

        /// <summary>
        /// Replace the base of a prefab instance from FBX to an existing wrapper.
        /// Uses ReplacePrefabAssetOfPrefabInstance with KeepAllPossibleOverrides.
        /// Must be called on a scene instance (not asset).
        /// </summary>
        public static bool ReplaceBaseWithWrapper(GameObject sceneInstance, string wrapperPath)
        {
            var wrapperAsset = AssetDatabase.LoadMainAssetAtPath(wrapperPath) as GameObject;
            if (wrapperAsset == null)
            {
                Debug.LogError($"[Override Doctor] Cannot load wrapper at {wrapperPath}");
                return false;
            }

            Undo.SetCurrentGroupName("Override Doctor: Replace FBX base with wrapper");

#if UNITY_2022_1_OR_NEWER
            var settings = new PrefabUtility.PrefabReplacingSettings
            {
                objectMatchMode = PrefabUtility.PrefabReplacingSettings.ObjectMatchMode.ByHierarchy,
                prefabOverridesOptions = PrefabUtility.PrefabReplacingSettings
                    .PrefabOverridesOptions.KeepAllPossibleOverrides
            };

            PrefabUtility.ReplacePrefabAssetOfPrefabInstance(
                sceneInstance, wrapperAsset, settings, InteractionMode.UserAction);
#else
            // Fallback for older Unity: manual replacement
            Debug.LogWarning("[Override Doctor] ReplacePrefabAssetOfPrefabInstance " +
                             "with settings requires Unity 2022.1+. " +
                             "Please replace manually or upgrade Unity.");
            return false;
#endif

            Debug.Log($"[Override Doctor] Replaced base of '{sceneInstance.name}' " +
                      $"with wrapper '{wrapperPath}'");
            return true;
        }

        // ── Remove Missing Scripts ─────────────────────────────────

        /// <summary>
        /// Remove all missing scripts from a prefab asset.
        /// Uses EditPrefabContentsScope for safe modification.
        /// Returns count removed.
        /// </summary>
        public static int RemoveMissingScripts(string prefabPath)
        {
            int removed = 0;

            using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
            {
                var root = scope.prefabContentsRoot;
                RemoveMissingScriptsRecursive(root.transform, ref removed);
            }

            if (removed > 0)
                Debug.Log($"[Override Doctor] Removed {removed} missing scripts from {prefabPath}");

            return removed;
        }

        private static void RemoveMissingScriptsRecursive(Transform t, ref int count)
        {
#if UNITY_2019_1_OR_NEWER
            int c = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
            if (c > 0)
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                count += c;
            }
#endif
            foreach (Transform child in t)
                RemoveMissingScriptsRecursive(child, ref count);
        }

        /// <summary>
        /// Batch remove missing scripts from multiple prefabs.
        /// </summary>
        public static int BatchRemoveMissingScripts(IEnumerable<string> prefabPaths)
        {
            int total = 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in prefabPaths)
                    total += RemoveMissingScripts(path);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            return total;
        }

        // ── Remove Unused Overrides ────────────────────────────────

        /// <summary>
        /// Remove unused overrides from a prefab asset.
        /// Uses built-in API on 2022.2+, falls back to manual cleanup.
        /// </summary>
        public static int RemoveUnusedOverrides(string prefabPath)
        {
            var prefab = AssetDatabase.LoadMainAssetAtPath(prefabPath) as GameObject;
            if (prefab == null) return 0;

#if UNITY_2022_2_OR_NEWER
            // Use built-in API
            int beforeCount = CountOverrides(prefab);
            PrefabUtility.RemoveUnusedOverrides(
                new[] { prefab }, InteractionMode.AutomatedAction);
            int afterCount = CountOverrides(prefab);
            int removed = beforeCount - afterCount;

            if (removed > 0)
                Debug.Log($"[Override Doctor] Removed {removed} unused overrides from {prefabPath}");
            return removed;
#else
            // Manual fallback: remove overrides with invalid property paths
            return RemoveUnusedOverridesManual(prefab);
#endif
        }

        private static int RemoveUnusedOverridesManual(GameObject prefab)
        {
            var mods = PrefabUtility.GetPropertyModifications(prefab);
            if (mods == null) return 0;

            var keep = new List<PropertyModification>();
            int removed = 0;

            foreach (var mod in mods)
            {
                if (mod.target == null)
                {
                    removed++;
                    continue;
                }

                // Check if property path is still valid
                try
                {
                    var so = new SerializedObject(mod.target);
                    var prop = so.FindProperty(mod.propertyPath);
                    if (prop == null && !PrefabUtility.IsDefaultOverride(mod))
                    {
                        removed++;
                        continue;
                    }
                }
                catch
                {
                    removed++;
                    continue;
                }

                keep.Add(mod);
            }

            if (removed > 0)
            {
                PrefabUtility.SetPropertyModifications(prefab, keep.ToArray());
                Debug.Log($"[Override Doctor] Removed {removed} unused overrides (manual) " +
                          $"from {AssetDatabase.GetAssetPath(prefab)}");
            }

            return removed;
        }

        /// <summary>
        /// Batch remove unused overrides from multiple prefabs.
        /// </summary>
        public static int BatchRemoveUnusedOverrides(IEnumerable<string> prefabPaths)
        {
            int total = 0;

#if UNITY_2022_2_OR_NEWER
            // Built-in batch API
            var roots = new List<GameObject>();
            foreach (var path in prefabPaths)
            {
                var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
                if (prefab != null) roots.Add(prefab);
            }

            if (roots.Count > 0)
            {
                int before = roots.Sum(CountOverrides);
                PrefabUtility.RemoveUnusedOverrides(
                    roots.ToArray(), InteractionMode.AutomatedAction);
                int after = roots.Sum(CountOverrides);
                total = before - after;
            }
#else
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in prefabPaths)
                    total += RemoveUnusedOverrides(path);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
#endif

            if (total > 0)
                Debug.Log($"[Override Doctor] Batch removed {total} unused overrides " +
                          $"from {prefabPaths.Count()} prefabs");

            return total;
        }

        // ── Helpers ────────────────────────────────────────────────

        private static int CountOverrides(GameObject prefab)
        {
            var mods = PrefabUtility.GetPropertyModifications(prefab);
            return mods?.Length ?? 0;
        }
    }
}
