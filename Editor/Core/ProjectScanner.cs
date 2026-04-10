using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SashaRX.OverrideDoctor
{
    /// <summary>
    /// Scans all prefab assets in the project (or a folder scope) for health issues:
    /// FBX-based without wrapper, missing scripts, broken references, unused overrides,
    /// bad materials, problematic import settings.
    ///
    /// Heavy operation — use ScanIncremental for non-blocking UI.
    /// </summary>
    internal class ProjectScanner
    {
        // ── Configuration ──────────────────────────────────────────

        /// <summary>Folder scope. null = entire Assets/.</summary>
        public string FolderScope;

        /// <summary>Also scan scene files for prefab instances.</summary>
        public bool IncludeScenes = false;

        /// <summary>Run FBX import audit (requires loading prefabs).</summary>
        public bool AuditFbxImport = true;

        /// <summary>Check materials for error/unsupported shaders.</summary>
        public bool CheckMaterials = true;

        // ── Full scan ──────────────────────────────────────────────

        public ProjectScanReport Scan()
        {
            var sw = Stopwatch.StartNew();
            var report = new ProjectScanReport();
            report.ScanScope = FolderScope ?? "Entire Project";

            // Phase 1: Find all prefab assets
            string filter = "t:Prefab";
            string[] searchFolders = FolderScope != null
                ? new[] { FolderScope }
                : new[] { "Assets" };

            string[] guids = AssetDatabase.FindAssets(filter, searchFolders);
            report.TotalPrefabs = guids.Length;

            // Phase 2: Build FBX → wrapper index (first pass: identify all FBX-based prefabs)
            var fbxIndex = BuildFbxWrapperIndex(guids);
            report.FbxToWrappersIndex = fbxIndex;

            // Phase 3: Analyze each prefab
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var result = AnalyzePrefab(path, fbxIndex);
                if (result != null)
                {
                    report.Results.Add(result);
                    TallyCategory(report, result);
                }
            }

            // Sort: worst first
            report.Results.Sort((a, b) =>
            {
                int catA = CategorySeverity(a.PrimaryCategory);
                int catB = CategorySeverity(b.PrimaryCategory);
                if (catA != catB) return catB.CompareTo(catA);
                return b.OverrideCount.CompareTo(a.OverrideCount);
            });

            report.IsComplete = true;
            report.ScanTimeMs = sw.ElapsedMilliseconds;
            return report;
        }

        // ── Incremental scan ───────────────────────────────────────

        /// <summary>
        /// Incremental scan. Yields progress [0..1].
        /// Use with EditorApplication.update pump.
        /// </summary>
        public IEnumerator<float> ScanIncremental(ProjectScanReport report,
            int batchSize = 20)
        {
            var sw = Stopwatch.StartNew();
            report.ScanScope = FolderScope ?? "Entire Project";

            string[] searchFolders = FolderScope != null
                ? new[] { FolderScope }
                : new[] { "Assets" };

            string[] guids = AssetDatabase.FindAssets("t:Prefab", searchFolders);
            report.TotalPrefabs = guids.Length;

            // Build index (fast — no asset loading)
            var fbxIndex = BuildFbxWrapperIndex(guids);
            report.FbxToWrappersIndex = fbxIndex;

            yield return 0.1f; // index built

            // Analyze each prefab
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var result = AnalyzePrefab(path, fbxIndex);
                if (result != null)
                {
                    report.Results.Add(result);
                    TallyCategory(report, result);
                }

                if (i % batchSize == 0)
                    yield return 0.1f + 0.9f * ((float)i / guids.Length);
            }

            report.Results.Sort((a, b) =>
            {
                int catA = CategorySeverity(a.PrimaryCategory);
                int catB = CategorySeverity(b.PrimaryCategory);
                if (catA != catB) return catB.CompareTo(catA);
                return b.OverrideCount.CompareTo(a.OverrideCount);
            });

            report.IsComplete = true;
            report.ScanTimeMs = sw.ElapsedMilliseconds;
            yield return 1f;
        }

        // ── FBX → Wrapper Index ────────────────────────────────────

        /// <summary>
        /// Build index: for each FBX/model asset, which prefab assets are
        /// Prefab Variants based on it (i.e. "wrappers").
        /// </summary>
        private Dictionary<string, List<string>> BuildFbxWrapperIndex(string[] prefabGuids)
        {
            var index = new Dictionary<string, List<string>>();

            foreach (var guid in prefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // Check what this prefab's base is
                // Use dependencies: if prefab depends on a .fbx, it might be a wrapper
                string[] deps = AssetDatabase.GetDependencies(path, false);
                foreach (var dep in deps)
                {
                    if (!IsModelAsset(dep)) continue;

                    // This prefab depends on a model file — could be a wrapper
                    // Verify: load and check if it's a direct Prefab Variant of the model
                    var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
                    if (prefab == null) continue;

                    var source = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
                    if (source == null) continue;

                    string sourcePath = AssetDatabase.GetAssetPath(source);
                    if (sourcePath == dep)
                    {
                        // This is a direct wrapper/variant of the model
                        if (!index.TryGetValue(dep, out var list))
                        {
                            list = new List<string>();
                            index[dep] = list;
                        }
                        list.Add(path);
                    }
                }
            }

            return index;
        }

        // ── Per-Prefab Analysis ────────────────────────────────────

        private PrefabScanResult AnalyzePrefab(string assetPath,
            Dictionary<string, List<string>> fbxIndex)
        {
            var result = new PrefabScanResult
            {
                AssetPath = assetPath,
                DisplayName = Path.GetFileNameWithoutExtension(assetPath),
                AllCategories = new List<PrefabHealthCategory>(),
                ExistingWrapperPaths = new List<string>(),
                ImportIssues = new List<FbxImportIssue>(),
                BadMaterials = new List<BadMaterialEntry>()
            };

            // Load the prefab asset (not instantiate)
            var prefab = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
            if (prefab == null)
            {
                result.PrimaryCategory = PrefabHealthCategory.Broken;
                result.AllCategories.Add(PrefabHealthCategory.Broken);
                return result;
            }

            // ── Check: FBX-based? ────────────────────────────────
            CheckFbxBase(prefab, assetPath, fbxIndex, result);

            // ── Check: Missing scripts ───────────────────────────
            CheckMissingScripts(prefab, result);

            // ── Check: Broken references (null targets in overrides) ─
            CheckBrokenReferences(prefab, result);

            // ── Check: Unused overrides ──────────────────────────
            CheckUnusedOverrides(prefab, result);

            // ── Check: Bad materials ─────────────────────────────
            if (CheckMaterials)
                CheckBadMaterials(prefab, result);

            // ── Check: FBX import audit ──────────────────────────
            if (AuditFbxImport && result.BaseFbxPath != null)
            {
                result.ImportIssues = FbxImportAuditor.QuickAudit(result.BaseFbxPath);
                if (result.ImportIssues.Count > 0)
                    result.AllCategories.Add(PrefabHealthCategory.FbxImportNoise);
            }

            // ── Count total overrides ────────────────────────────
            var mods = PrefabUtility.GetPropertyModifications(prefab);
            result.OverrideCount = mods?.Length ?? 0;

            // ── Determine primary category ───────────────────────
            result.PrimaryCategory = result.AllCategories.Count > 0
                ? result.AllCategories.OrderByDescending(CategorySeverity).First()
                : PrefabHealthCategory.Clean;

            if (result.PrimaryCategory == PrefabHealthCategory.Clean &&
                result.AllCategories.Count == 0)
            {
                return null; // skip clean prefabs to reduce noise
            }

            return result;
        }

        // ── Individual checks ──────────────────────────────────────

        private void CheckFbxBase(GameObject prefab, string prefabPath,
            Dictionary<string, List<string>> fbxIndex, PrefabScanResult result)
        {
            // Walk to the original source to find if base is a model
            var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(prefab);
            if (original == null) return;

            string originalPath = AssetDatabase.GetAssetPath(original);
            if (!IsModelAsset(originalPath)) return;

            result.BaseFbxPath = originalPath;

            // Check if this prefab IS a direct wrapper (Variant of the model)
            var directSource = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
            if (directSource != null)
            {
                string directSourcePath = AssetDatabase.GetAssetPath(directSource);
                if (IsModelAsset(directSourcePath))
                {
                    // This prefab directly references FBX as base
                    // Check if wrappers exist for this FBX
                    if (fbxIndex.TryGetValue(directSourcePath, out var wrappers))
                    {
                        // Exclude self
                        var others = wrappers.Where(w => w != prefabPath).ToList();
                        if (others.Count > 0)
                        {
                            result.ExistingWrapperPaths = others;
                            result.AllCategories.Add(PrefabHealthCategory.FbxHasWrapper);
                            return;
                        }
                    }

                    result.AllCategories.Add(PrefabHealthCategory.FbxWithoutWrapper);
                }
            }
        }

        private void CheckMissingScripts(GameObject prefab, PrefabScanResult result)
        {
            int count = 0;
            CountMissingScriptsRecursive(prefab.transform, ref count);
            if (count > 0)
            {
                result.MissingScriptCount = count;
                result.AllCategories.Add(PrefabHealthCategory.MissingScripts);
            }
        }

        private static void CountMissingScriptsRecursive(Transform t, ref int count)
        {
#if UNITY_2019_1_OR_NEWER
            count += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
#else
            foreach (var comp in t.gameObject.GetComponents<Component>())
            {
                if (comp == null) count++;
            }
#endif
            foreach (Transform child in t)
                CountMissingScriptsRecursive(child, ref count);
        }

        private void CheckBrokenReferences(GameObject prefab, PrefabScanResult result)
        {
            var mods = PrefabUtility.GetPropertyModifications(prefab);
            if (mods == null) return;

            int broken = 0;
            foreach (var mod in mods)
            {
                if (mod.target == null && !string.IsNullOrEmpty(mod.propertyPath))
                {
                    broken++;
                }
            }

            if (broken > 0)
            {
                result.BrokenReferenceCount = broken;
                result.AllCategories.Add(PrefabHealthCategory.BrokenReferences);
            }
        }

        private void CheckUnusedOverrides(GameObject prefab, PrefabScanResult result)
        {
            // Unused overrides = overrides where property path no longer exists
            // on the target object (field renamed, SerializeReference removed, etc.)
            // For full detection we'd need SerializedObject per component —
            // expensive. Use lightweight heuristic: null target = broken ref (above),
            // but also check for common patterns.
            var mods = PrefabUtility.GetPropertyModifications(prefab);
            if (mods == null) return;

            int unused = 0;
            foreach (var mod in mods)
            {
                if (mod.target == null) continue; // already counted as broken ref
                if (PrefabUtility.IsDefaultOverride(mod)) continue;

                // Try to verify property exists on target
                try
                {
                    var so = new SerializedObject(mod.target);
                    var prop = so.FindProperty(mod.propertyPath);
                    if (prop == null)
                    {
                        unused++;
                    }
                }
                catch
                {
                    // SerializedObject creation can fail for destroyed objects
                    unused++;
                }
            }

            if (unused > 0)
            {
                result.UnusedOverrideCount = unused;
                result.AllCategories.Add(PrefabHealthCategory.UnusedOverrides);
            }
        }

        private void CheckBadMaterials(GameObject prefab, PrefabScanResult result)
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                foreach (var mat in materials)
                {
                    if (mat == null)
                    {
                        result.BadMaterials.Add(new BadMaterialEntry
                        {
                            MaterialName = "(null)",
                            MaterialPath = "",
                            ShaderName = "",
                            Reason = "null material reference"
                        });
                        continue;
                    }

                    if (mat.shader == null)
                    {
                        result.BadMaterials.Add(new BadMaterialEntry
                        {
                            MaterialName = mat.name,
                            MaterialPath = AssetDatabase.GetAssetPath(mat),
                            ShaderName = "(null)",
                            Reason = "null shader"
                        });
                        continue;
                    }

                    if (mat.shader.name == "Hidden/InternalErrorShader")
                    {
                        result.BadMaterials.Add(new BadMaterialEntry
                        {
                            MaterialName = mat.name,
                            MaterialPath = AssetDatabase.GetAssetPath(mat),
                            ShaderName = mat.shader.name,
                            Reason = "error shader (missing/broken)"
                        });
                    }
                    else if (!mat.shader.isSupported)
                    {
                        result.BadMaterials.Add(new BadMaterialEntry
                        {
                            MaterialName = mat.name,
                            MaterialPath = AssetDatabase.GetAssetPath(mat),
                            ShaderName = mat.shader.name,
                            Reason = "unsupported shader"
                        });
                    }
                }
            }

            if (result.BadMaterials.Count > 0)
                result.AllCategories.Add(PrefabHealthCategory.BadMaterials);
        }

        // ── Helpers ────────────────────────────────────────────────

        private static bool IsModelAsset(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".fbx" or ".obj" or ".blend" or ".gltf" or ".glb"
                or ".dae" or ".3ds" or ".max" or ".ma" or ".mb";
        }

        private static int CategorySeverity(PrefabHealthCategory cat) => cat switch
        {
            PrefabHealthCategory.Broken => 100,
            PrefabHealthCategory.MissingScripts => 90,
            PrefabHealthCategory.BrokenReferences => 80,
            PrefabHealthCategory.FbxWithoutWrapper => 70,
            PrefabHealthCategory.BadMaterials => 60,
            PrefabHealthCategory.UnusedOverrides => 50,
            PrefabHealthCategory.FbxImportNoise => 40,
            PrefabHealthCategory.FbxHasWrapper => 30,
            PrefabHealthCategory.Clean => 0,
            _ => 0
        };

        private static void TallyCategory(ProjectScanReport report, PrefabScanResult result)
        {
            foreach (var cat in result.AllCategories)
            {
                switch (cat)
                {
                    case PrefabHealthCategory.FbxWithoutWrapper: report.FbxWithoutWrapper++; break;
                    case PrefabHealthCategory.FbxHasWrapper: report.FbxHasWrapper++; break;
                    case PrefabHealthCategory.Broken: report.Broken++; break;
                    case PrefabHealthCategory.MissingScripts: report.MissingScripts++; break;
                    case PrefabHealthCategory.BrokenReferences: report.BrokenReferences++; break;
                    case PrefabHealthCategory.FbxImportNoise: report.FbxImportNoise++; break;
                    case PrefabHealthCategory.BadMaterials: report.BadMaterialCount++; break;
                    case PrefabHealthCategory.UnusedOverrides: report.UnusedOverrides++; break;
                }
            }
        }
    }
}
