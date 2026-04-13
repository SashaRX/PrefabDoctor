using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// One level in the prefab nesting chain.
    /// Depth 0 = outermost (scene instance or top-level prefab being analyzed).
    /// Depth N = base prefab asset.
    /// </summary>
    internal struct NestingLevel
    {
        public int Depth;
        public GameObject Root;
        public string AssetPath; // empty for scene instances
        public bool IsSceneInstance;
    }

    /// <summary>
    /// A single property override at a specific nesting depth.
    /// Value is always stored as string (from PropertyModification.value).
    /// </summary>
    internal struct OverrideEntry
    {
        public int Depth;
        public string Value;
        public string AssetPath;
        public Object ObjectReference; // for reference-type overrides
    }

    /// <summary>
    /// Canonical identifier for a property across nesting levels.
    /// (ComponentType, GameObjectPath, PropertyPath) alone is not enough:
    /// a single GameObject can host several components of the same type
    /// (e.g. multiple FishNet NetworkBehaviours whose base class name
    /// collides), and those would otherwise merge into one PropertyConflict
    /// with parallel OverrideEntries at the same depth. TargetInstanceId
    /// breaks that tie — it is NOT part of DisplayName so the user-facing
    /// label still reads cleanly.
    /// </summary>
    internal struct PropertyKey
    {
        public string ComponentType;
        public string GameObjectPath; // relative path within prefab
        public string PropertyPath;
        public int TargetInstanceId;  // disambiguates same-typed sibling components

        public string DisplayName =>
            $"{GameObjectPath}/{ComponentType}::{PropertyPath}";

        public override int GetHashCode() =>
            (ComponentType, GameObjectPath, PropertyPath, TargetInstanceId).GetHashCode();

        public override bool Equals(object obj) =>
            obj is PropertyKey other &&
            ComponentType == other.ComponentType &&
            GameObjectPath == other.GameObjectPath &&
            PropertyPath == other.PropertyPath &&
            TargetInstanceId == other.TargetInstanceId;

        public override string ToString() => DisplayName;
    }

    internal enum ConflictSeverity
    {
        /// <summary>Override exists but value matches source within epsilon.</summary>
        Insignificant,

        /// <summary>Orphaned override — target component no longer exists.</summary>
        Orphan,

        /// <summary>Property overridden at 2+ levels (no ping-pong).</summary>
        MultiOverride,

        /// <summary>Value goes A→B→A across levels — classic ping-pong.</summary>
        PingPong
    }

    /// <summary>
    /// Semantic grouping of overridden properties. Severity tells how bad the
    /// override is; Category tells what kind of data it is (lightmap noise,
    /// network noise, transform, etc.) so the UI can filter/summarise it.
    /// </summary>
    internal enum OverrideCategory
    {
        General,
        Transform,
        Lightmap,
        NetworkNoise,
        StaticFlags,
        Name,
        Material
    }

    /// <summary>
    /// Full analysis result for one property across the entire nesting chain.
    /// </summary>
    internal class PropertyConflict
    {
        public PropertyKey Key;
        public ConflictSeverity Severity;
        public OverrideCategory Category;
        public List<OverrideEntry> Overrides = new();

        /// <summary>
        /// For ping-pong: indices into Overrides showing the A→B→A pattern.
        /// </summary>
        public (int first, int middle, int pingBack) PingPongIndices;
    }

    /// <summary>
    /// Aggregated analysis for one GameObject across the nesting chain.
    /// </summary>
    internal class GameObjectReport
    {
        public string RelativePath;
        public GameObject Instance; // may be null if analyzing asset
        public GameObject InstanceRoot; // hierarchy mode: scene PrefabInstance root
        public List<PropertyConflict> Conflicts = new();

        public int PingPongCount;
        public int MultiOverrideCount;
        public int InsignificantCount;
        public int OrphanCount;
    }

    /// <summary>
    /// Top-level analysis result for the entire prefab.
    /// </summary>
    internal class AnalysisReport
    {
        public GameObject AnalyzedRoot;
        public List<NestingLevel> Chain = new();
        public List<GameObjectReport> GameObjects = new();

        public int TotalPingPong;
        public int TotalMultiOverride;
        public int TotalInsignificant;
        public int TotalOrphan;

        public bool IsComplete;
        public float AnalysisTimeMs;

        /// <summary>True if this report was built with recursive hierarchy analysis.</summary>
        public bool IsHierarchyMode;

        /// <summary>Number of PrefabInstance roots analyzed (hierarchy mode).</summary>
        public int InstancesAnalyzed;

        /// <summary>
        /// Every PrefabInstance root touched by this hierarchy run. Populated
        /// only when <see cref="IsHierarchyMode"/> is true. Used by the
        /// bulk Clean Orphans button in the window so it can iterate all
        /// real scene GameObjects without re-walking the hierarchy.
        /// </summary>
        public List<GameObject> HierarchyInstanceRoots = new();

        /// <summary>
        /// Mapping from <see cref="GameObjectReport.RelativePath"/> to the
        /// PrefabInstance root that owns it. Populated only in hierarchy
        /// mode. Used by <c>ResolveBatchTasks</c> to dispatch each
        /// conflict to the correct nested instance root for
        /// <see cref="OverrideActions.BatchRevert"/>, bypassing the
        /// expensive and fragile <c>ResolveByRelativePath</c> path walk.
        /// </summary>
        public Dictionary<string, GameObject> GoPathToInstanceRoot;

        /// <summary>
        /// Unique prefab asset paths discovered during analysis (from chain
        /// NestingLevels). Populated in both instance and hierarchy modes.
        /// Used to scope the dependency health scan to relevant assets only.
        /// </summary>
        public HashSet<string> DependentAssetPaths = new();

        /// <summary>
        /// Maps source prefab asset path → list of scene instance roots.
        /// Populated in hierarchy mode. Used by the UI to group the left
        /// panel by prefab type instead of individual child GameObjects.
        /// </summary>
        public Dictionary<string, List<GameObject>> AssetToInstances;

        /// <summary>
        /// Maps scene instance root → source prefab asset path.
        /// Populated in hierarchy mode.
        /// </summary>
        public Dictionary<GameObject, string> InstanceToAsset;
    }

    /// <summary>
    /// Groups all analysis data for one prefab asset type across all scene
    /// instances. Built from AnalysisReport for the UI layer.
    /// </summary>
    internal class PrefabTypeGroup
    {
        public string AssetPath;
        public string DisplayName;
        public List<GameObject> Instances = new();
        public List<GameObjectReport> ChildReports = new();
        public int TotalConflicts;
        public int PingPongCount;
        public int MultiOverrideCount;
        public int OrphanCount;
        public int InsignificantCount;
    }
}
