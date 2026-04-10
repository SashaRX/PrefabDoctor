using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Analyzes ModelImporter settings and compares them against
    /// actual prefab usage to find settings that generate unnecessary overrides.
    /// </summary>
    internal static class FbxImportAuditor
    {
        /// <summary>
        /// Audit a FBX-based prefab: check if import settings are generating
        /// unnecessary overrides given how the prefab is actually used.
        /// </summary>
        /// <param name="fbxPath">Path to the FBX/model asset.</param>
        /// <param name="prefabRoot">Root of the prefab instance or asset to check against.</param>
        /// <returns>List of issues found.</returns>
        public static List<FbxImportIssue> Audit(string fbxPath, GameObject prefabRoot)
        {
            var issues = new List<FbxImportIssue>();

            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null) return issues;

            CheckImportMaterials(importer, prefabRoot, issues);
            CheckImportAnimation(importer, prefabRoot, issues);
            CheckImportCamerasLights(importer, prefabRoot, issues);
            CheckScaleFactor(importer, issues);
            CheckNormals(importer, issues);

            return issues;
        }

        /// <summary>
        /// Quick audit without loading prefab — just checks import settings
        /// for obviously wasteful defaults.
        /// </summary>
        public static List<FbxImportIssue> QuickAudit(string fbxPath)
        {
            var issues = new List<FbxImportIssue>();

            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null) return issues;

            if (importer.importAnimation)
            {
                // Check if FBX actually contains animation clips
                var clips = importer.clipAnimations;
                var defaultClips = importer.defaultClipAnimations;
                if ((clips == null || clips.Length == 0) &&
                    (defaultClips == null || defaultClips.Length == 0))
                {
                    issues.Add(new FbxImportIssue
                    {
                        Setting = "importAnimation",
                        CurrentValue = "true",
                        Reason = "FBX contains no animation clips but importAnimation is on — " +
                                 "creates empty Animator component, often disabled via override",
                        Suggestion = "Set importAnimation = false"
                    });
                }
            }

            if (importer.importCameras)
            {
                issues.Add(new FbxImportIssue
                {
                    Setting = "importCameras",
                    CurrentValue = "true",
                    Reason = "Cameras in FBX create extra GameObjects, usually removed in prefab as overrides",
                    Suggestion = "Set importCameras = false unless cameras are intentional"
                });
            }

            if (importer.importLights)
            {
                issues.Add(new FbxImportIssue
                {
                    Setting = "importLights",
                    CurrentValue = "true",
                    Reason = "Lights in FBX create extra GameObjects, usually removed in prefab as overrides",
                    Suggestion = "Set importLights = false unless lights are intentional"
                });
            }

            return issues;
        }

        // ── Detailed checks (require loaded prefab) ───────────────

        private static void CheckImportMaterials(ModelImporter importer,
            GameObject prefabRoot, List<FbxImportIssue> issues)
        {
            if (importer.materialImportMode == ModelImporterMaterialImportMode.None) return;

            // Count how many renderers have ALL materials overridden
            var renderers = prefabRoot.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;

            int totalSlots = 0;
            int overriddenSlots = 0;

            foreach (var renderer in renderers)
            {
                var so = new SerializedObject(renderer);
                var matArray = so.FindProperty("m_Materials");
                if (matArray == null || !matArray.isArray) continue;

                for (int i = 0; i < matArray.arraySize; i++)
                {
                    totalSlots++;
                    var element = matArray.GetArrayElementAtIndex(i);
                    if (element.prefabOverride)
                        overriddenSlots++;
                }
            }

            if (totalSlots > 0 && overriddenSlots == totalSlots)
            {
                issues.Add(new FbxImportIssue
                {
                    Setting = "importMaterials",
                    CurrentValue = "true",
                    Reason = $"All {totalSlots} material slots are overridden in prefab — " +
                             "FBX-imported materials are never used",
                    Suggestion = "Set importMaterials = false to eliminate material overrides"
                });
            }
            else if (totalSlots > 0 && overriddenSlots > totalSlots / 2)
            {
                issues.Add(new FbxImportIssue
                {
                    Setting = "importMaterials",
                    CurrentValue = "true",
                    Reason = $"{overriddenSlots}/{totalSlots} material slots overridden — " +
                             "majority of FBX materials unused",
                    Suggestion = "Consider remapping materials in import settings " +
                                 "or disabling importMaterials"
                });
            }
        }

        private static void CheckImportAnimation(ModelImporter importer,
            GameObject prefabRoot, List<FbxImportIssue> issues)
        {
            if (!importer.importAnimation) return;

            // Check if Animator exists and is disabled via override
            var animator = prefabRoot.GetComponentInChildren<Animator>(true);
            if (animator == null) return;

            var so = new SerializedObject(animator);
            var enabledProp = so.FindProperty("m_Enabled");
            if (enabledProp != null && enabledProp.prefabOverride && !enabledProp.boolValue)
            {
                issues.Add(new FbxImportIssue
                {
                    Setting = "importAnimation",
                    CurrentValue = "true",
                    Reason = "Animator component is disabled via override — animation import is wasted",
                    Suggestion = "Set importAnimation = false to remove Animator entirely"
                });
            }
        }

        private static void CheckImportCamerasLights(ModelImporter importer,
            GameObject prefabRoot, List<FbxImportIssue> issues)
        {
            if (!importer.importCameras && !importer.importLights) return;

            // Check for RemovedGameObject overrides that target cameras/lights
            if (!PrefabUtility.IsPartOfPrefabInstance(prefabRoot)) return;

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(prefabRoot);
            if (root == null) root = prefabRoot;

            var removedGOs = PrefabUtility.GetRemovedGameObjects(root);
            foreach (var removed in removedGOs)
            {
                var go = removed.assetGameObject;
                if (go == null) continue;

                if (importer.importCameras && go.GetComponent<Camera>() != null)
                {
                    issues.Add(new FbxImportIssue
                    {
                        Setting = "importCameras",
                        CurrentValue = "true",
                        Reason = $"Camera '{go.name}' from FBX is removed via override",
                        Suggestion = "Set importCameras = false"
                    });
                }

                if (importer.importLights && go.GetComponent<Light>() != null)
                {
                    issues.Add(new FbxImportIssue
                    {
                        Setting = "importLights",
                        CurrentValue = "true",
                        Reason = $"Light '{go.name}' from FBX is removed via override",
                        Suggestion = "Set importLights = false"
                    });
                }
            }
        }

        private static void CheckScaleFactor(ModelImporter importer,
            List<FbxImportIssue> issues)
        {
            // scaleFactor != 1.0 means FBX root gets compensating scale
            // This often leads to scale overrides in prefabs
            if (Mathf.Abs(importer.globalScale - 1f) > 0.001f &&
                importer.useFileScale)
            {
                issues.Add(new FbxImportIssue
                {
                    Setting = "globalScale / useFileScale",
                    CurrentValue = $"globalScale={importer.globalScale}, useFileScale=true",
                    Reason = "Non-unit scale factor creates compensating Transform.localScale " +
                             "on FBX root — often overridden in prefabs",
                    Suggestion = "Export FBX at correct scale from DCC, or adjust fileScale " +
                                 "to avoid runtime compensation"
                });
            }
        }

        private static void CheckNormals(ModelImporter importer,
            List<FbxImportIssue> issues)
        {
            // importNormals = Calculate when FBX has normals = wasted computation
            // Not directly an override issue, but flaggable
            if (importer.importNormals == ModelImporterNormals.Calculate &&
                importer.importTangents == ModelImporterTangents.CalculateMikk)
            {
                // This is just informational, not an override problem
                // Don't add to issues unless it causes actual problems
            }
        }
    }
}
