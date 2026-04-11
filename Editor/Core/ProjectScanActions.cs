using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SashaRX.PrefabDoctor
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
                Debug.LogError($"[Prefab Doctor] Cannot load model at {fbxPath}");
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
                    Debug.Log($"[Prefab Doctor] Created wrapper: {wrapperPath} → base: {fbxPath}");
                    return wrapperPath;
                }
                else
                {
                    Debug.LogError($"[Prefab Doctor] Failed to save wrapper at {wrapperPath}");
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
                Debug.LogError($"[Prefab Doctor] Cannot load wrapper at {wrapperPath}");
                return false;
            }

            Undo.SetCurrentGroupName("Prefab Doctor: Replace FBX base with wrapper");

            // ReplacePrefabAssetOfPrefabInstance with PrefabReplacingSettings
            // requires Unity 2022.3+ and may not be available in all versions.
            // Use the simpler overload that exists since 2021.
            PrefabUtility.ReplacePrefabAssetOfPrefabInstance(
                sceneInstance, wrapperAsset, InteractionMode.UserAction);

#if FALSE // PrefabReplacingSettings — enable when targeting Unity 2023+
            // var settings = new PrefabReplacingSettings { ... };
#endif

#if FALSE
            // Fallback for older Unity: manual replacement
            Debug.LogWarning("[Prefab Doctor] ReplacePrefabAssetOfPrefabInstance " +
                             "with settings requires Unity 2022.1+. " +
                             "Please replace manually or upgrade Unity.");
            return false;
#endif

            Debug.Log($"[Prefab Doctor] Replaced base of '{sceneInstance.name}' " +
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
                Debug.Log($"[Prefab Doctor] Removed {removed} missing scripts from {prefabPath}");

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
        /// Remove unused overrides from a prefab asset, including nested
        /// PrefabInstance children. Opens the prefab in isolation via
        /// <see cref="PrefabUtility.EditPrefabContentsScope"/>, collects
        /// every nested instance root, and hands them to Unity's built-in
        /// <see cref="PrefabUtility.RemoveUnusedOverrides"/> (the same
        /// path as the Hierarchy right-click menu). Returns the total
        /// number of modifications removed across all instance roots
        /// inside the file.
        ///
        /// Deep cleanup: a single call reaches orphans at any nesting
        /// depth. The scope's <c>Dispose</c> re-saves the asset.
        /// </summary>
        public static int RemoveUnusedOverrides(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath)) return 0;
            if (!prefabPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                return 0;

            int total = 0;
            try
            {
                using var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath);
                var root = scope.prefabContentsRoot;
                if (root == null) return 0;

                // Collect every nested prefab instance root inside this
                // loaded prefab. A variant prefab's own root is itself an
                // instance, so include it too.
                var instances = new List<GameObject>();
                if (PrefabUtility.IsAnyPrefabInstanceRoot(root))
                    instances.Add(root);
                CollectNestedInstanceRoots(root.transform, instances);

                if (instances.Count == 0) return 0;

#if UNITY_2022_2_OR_NEWER
                // Unity's built-in deep cleanup — same algorithm as the
                // Hierarchy right-click "Remove Unused Overrides" menu.
                int before = 0;
                for (int i = 0; i < instances.Count; i++) before += CountMods(instances[i]);

                PrefabUtility.RemoveUnusedOverrides(
                    instances.ToArray(), InteractionMode.AutomatedAction);

                int after = 0;
                for (int i = 0; i < instances.Count; i++) after += CountMods(instances[i]);
                total = before - after;
#else
                foreach (var inst in instances)
                    total += ManualCleanInstance(inst);
#endif
            }
            catch (System.Exception ex)
            {
                Debug.LogError(
                    $"[Prefab Doctor] Failed to clean {prefabPath}: {ex.Message}");
                return 0;
            }

            if (total > 0)
                Debug.Log($"[Prefab Doctor] Removed {total} unused overrides from {prefabPath}");
            return total;
        }

        /// <summary>
        /// Recursively walk <paramref name="parent"/>'s children and add every
        /// GameObject that is a prefab instance root to <paramref name="result"/>.
        /// </summary>
        private static void CollectNestedInstanceRoots(
            Transform parent, List<GameObject> result)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                    result.Add(child.gameObject);
                CollectNestedInstanceRoots(child, result);
            }
        }

        private static int CountMods(GameObject go)
        {
            var mods = PrefabUtility.GetPropertyModifications(go);
            return mods?.Length ?? 0;
        }

#if !UNITY_2022_2_OR_NEWER
        // Manual fallback for Unity < 2022.2 that lacks the public
        // PrefabUtility.RemoveUnusedOverrides API. Only handles the
        // target == null case; the 2022.2+ built-in also detects
        // stale propertyPaths and SerializeReference drift which we
        // cannot replicate without more SerializedObject gymnastics.
        private static int ManualCleanInstance(GameObject instance)
        {
            var mods = PrefabUtility.GetPropertyModifications(instance);
            if (mods == null || mods.Length == 0) return 0;

            var keep = new List<PropertyModification>(mods.Length);
            int removed = 0;
            foreach (var mod in mods)
            {
                if (mod.target == null) { removed++; continue; }
                keep.Add(mod);
            }

            if (removed > 0)
                PrefabUtility.SetPropertyModifications(instance, keep.ToArray());
            return removed;
        }
#endif

        /// <summary>
        /// Batch remove unused overrides from multiple prefabs. Uses
        /// <see cref="AssetDatabase.StartAssetEditing"/> so all writes are
        /// grouped into one refresh.
        /// </summary>
        public static int BatchRemoveUnusedOverrides(IEnumerable<string> prefabPaths)
        {
            if (prefabPaths == null) return 0;

            int total = 0;
            int processed = 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in prefabPaths)
                {
                    total += RemoveUnusedOverrides(path);
                    processed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log(
                $"[Prefab Doctor] Batch cleanup: processed {processed} prefab files, "
                + $"removed {total} unused override modifications total");
            return total;
        }

        /// <summary>
        /// Batch remove unused overrides from every prefab asset under the
        /// given folder (or "Assets" if null). Uses AssetDatabase to find
        /// paths, then calls <see cref="BatchRemoveUnusedOverrides"/> with
        /// an optional progress callback so the caller can drive a
        /// cancelable progress bar.
        /// </summary>
        public static int CleanAllUnusedOverridesInScope(
            string folderScope,
            System.Func<int, int, string, bool> onProgress = null)
        {
            string[] searchFolders = string.IsNullOrEmpty(folderScope)
                ? new[] { "Assets" }
                : new[] { folderScope };

            string[] guids = AssetDatabase.FindAssets("t:Prefab", searchFolders);
            if (guids.Length == 0) return 0;

            int total = 0;
            int processed = 0;
            bool cancelled = false;

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);

                    if (onProgress != null && onProgress(i, guids.Length, path))
                    {
                        cancelled = true;
                        break;
                    }

                    total += RemoveUnusedOverrides(path);
                    processed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log(
                $"[Prefab Doctor] Scope cleanup ({folderScope ?? "Assets"}): "
                + $"{(cancelled ? "cancelled after " : "processed ")}"
                + $"{processed} / {guids.Length} prefab files, "
                + $"removed {total} unused override modifications total");
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
