using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.OverrideDoctor
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
    /// ComponentType + ComponentName + PropertyPath uniquely identifies
    /// "the same" property regardless of which depth we're looking at.
    /// </summary>
    internal struct PropertyKey
    {
        public string ComponentType;
        public string GameObjectPath; // relative path within prefab
        public string PropertyPath;

        public string DisplayName =>
            $"{GameObjectPath}/{ComponentType}::{PropertyPath}";

        public override int GetHashCode() =>
            (ComponentType, GameObjectPath, PropertyPath).GetHashCode();

        public override bool Equals(object obj) =>
            obj is PropertyKey other &&
            ComponentType == other.ComponentType &&
            GameObjectPath == other.GameObjectPath &&
            PropertyPath == other.PropertyPath;

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
    /// Full analysis result for one property across the entire nesting chain.
    /// </summary>
    internal class PropertyConflict
    {
        public PropertyKey Key;
        public ConflictSeverity Severity;
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

        public bool IsComplete; // false if analysis was partial/interrupted
        public float AnalysisTimeMs;
    }
}
