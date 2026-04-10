using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.OverrideDoctor
{
    internal enum PrefabHealthCategory
    {
        /// <summary>Base asset is FBX/OBJ/etc. without a prefab wrapper.</summary>
        FbxWithoutWrapper,

        /// <summary>Base asset is FBX and a wrapper prefab already exists.</summary>
        FbxHasWrapper,

        /// <summary>Prefab instance is disconnected or base asset missing.</summary>
        Broken,

        /// <summary>Contains MonoBehaviours with missing scripts.</summary>
        MissingScripts,

        /// <summary>Contains references to destroyed/missing objects.</summary>
        BrokenReferences,

        /// <summary>FBX import settings creating unnecessary overrides.</summary>
        FbxImportNoise,

        /// <summary>Materials with error shader or unsupported shader.</summary>
        BadMaterials,

        /// <summary>Has unused overrides (invalid property paths, null targets).</summary>
        UnusedOverrides,

        /// <summary>Clean — no issues detected.</summary>
        Clean
    }

    internal struct FbxImportIssue
    {
        public string Setting;      // e.g. "importMaterials"
        public string CurrentValue; // e.g. "true"
        public string Reason;       // e.g. "All 4 materials are overridden in prefab"
        public string Suggestion;   // e.g. "Set importMaterials = false"
    }

    internal struct BadMaterialEntry
    {
        public string MaterialName;
        public string MaterialPath;
        public string ShaderName;
        public string Reason; // "error shader", "unsupported", "null"
    }

    /// <summary>
    /// Scan result for a single prefab asset.
    /// </summary>
    internal class PrefabScanResult
    {
        public string AssetPath;
        public string DisplayName;
        public PrefabHealthCategory PrimaryCategory;

        /// <summary>All detected issues (a prefab can have multiple).</summary>
        public List<PrefabHealthCategory> AllCategories = new();

        // FBX-specific
        public string BaseFbxPath;                         // null if not FBX-based
        public List<string> ExistingWrapperPaths;          // wrappers that reference same FBX
        public List<FbxImportIssue> ImportIssues;

        // Health
        public int MissingScriptCount;
        public int BrokenReferenceCount;
        public int UnusedOverrideCount;
        public List<BadMaterialEntry> BadMaterials;

        // Metadata
        public int OverrideCount;         // total overrides (for sorting)
        public int NestingDepth;          // how deep the nesting chain goes
    }

    /// <summary>
    /// Full project scan result.
    /// </summary>
    internal class ProjectScanReport
    {
        public List<PrefabScanResult> Results = new();
        public Dictionary<string, List<string>> FbxToWrappersIndex = new(); // fbxPath → wrapper prefab paths

        // Summary counts
        public int TotalPrefabs;
        public int FbxWithoutWrapper;
        public int FbxHasWrapper;
        public int Broken;
        public int MissingScripts;
        public int BrokenReferences;
        public int FbxImportNoise;
        public int BadMaterialCount;
        public int UnusedOverrides;
        public int Clean;

        public float ScanTimeMs;
        public bool IsComplete;
        public string ScanScope; // "All Prefabs", "Assets/Prefabs/...", etc.
    }
}
