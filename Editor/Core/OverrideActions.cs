using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Operations for resolving override conflicts.
    /// All operations support Undo via standard Unity Undo system.
    /// </summary>
    internal static class OverrideActions
    {
        /// <summary>
        /// Revert all overrides for a property at all depths except the specified one.
        /// Keeps only the override at keepDepth.
        /// </summary>
        public static void KeepOnlyAtDepth(GameObject root, PropertyConflict conflict, int keepDepth)
        {
            Undo.SetCurrentGroupName($"Prefab Doctor: Keep at depth {keepDepth}");
            int group = Undo.GetCurrentGroup();
            try
            {
                var chain = new OverrideAnalyzer().BuildChain(root);

                foreach (var entry in conflict.Overrides)
                {
                    if (entry.Depth == keepDepth) continue;

                    var level = chain.FirstOrDefault(l => l.Depth == entry.Depth);
                    if (level.Root == null) continue;

                    RemoveModification(level.Root, conflict.Key);
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
        }

        /// <summary>
        /// Revert all overrides for a property at all depths (return to base value).
        /// </summary>
        public static void RevertAll(GameObject root, PropertyConflict conflict)
        {
            Undo.SetCurrentGroupName("Prefab Doctor: Revert all");
            int group = Undo.GetCurrentGroup();
            try
            {
                var chain = new OverrideAnalyzer().BuildChain(root);

                foreach (var entry in conflict.Overrides)
                {
                    var level = chain.FirstOrDefault(l => l.Depth == entry.Depth);
                    if (level.Root == null) continue;

                    RemoveModification(level.Root, conflict.Key);
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
        }

        /// <summary>
        /// Remove all orphaned modifications (target == null) from a prefab instance.
        /// </summary>
        public static int CleanOrphans(GameObject root)
        {
            Undo.SetCurrentGroupName("Prefab Doctor: Clean orphans");
            int group = Undo.GetCurrentGroup();
            try
            {
                var mods = PrefabUtility.GetPropertyModifications(root);
                if (mods == null) return 0;

                Undo.RecordObject(root, "Clean orphans");

                var clean = mods.Where(m => m.target != null).ToArray();
                int removed = mods.Length - clean.Length;

                if (removed > 0)
                {
                    PrefabUtility.SetPropertyModifications(root, clean);
                }

                return removed;
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
        }

        /// <summary>
        /// Bulk variant of <see cref="CleanOrphans"/>: for every prefab
        /// instance root in <paramref name="instanceRoots"/>, read its
        /// PropertyModifications, drop every entry whose target is null,
        /// and write the cleaned array back. One Undo group for the entire
        /// batch so the whole operation can be reverted with a single
        /// Ctrl+Z. Returns the total number of removed modifications.
        /// </summary>
        public static int CleanOrphansHierarchy(IReadOnlyList<GameObject> instanceRoots)
        {
            if (instanceRoots == null || instanceRoots.Count == 0) return 0;

            Undo.SetCurrentGroupName("Prefab Doctor: Clean orphans (hierarchy)");
            int group = Undo.GetCurrentGroup();
            int totalRemoved = 0;

            try
            {
                for (int i = 0; i < instanceRoots.Count; i++)
                {
                    var root = instanceRoots[i];
                    if (root == null) continue;

                    var mods = PrefabUtility.GetPropertyModifications(root);
                    if (mods == null || mods.Length == 0) continue;

                    var clean = mods.Where(m => m.target != null).ToArray();
                    int removed = mods.Length - clean.Length;
                    if (removed == 0) continue;

                    Undo.RecordObject(root, "Clean orphans (hierarchy)");
                    PrefabUtility.SetPropertyModifications(root, clean);
                    totalRemoved += removed;
                }

                return totalRemoved;
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
        }

        /// <summary>
        /// Remove insignificant overrides — those where value matches source within epsilon.
        /// Returns count of removed overrides.
        /// </summary>
        public static int CleanInsignificant(GameObject root, List<NestingLevel> chain)
        {
            Undo.SetCurrentGroupName("Prefab Doctor: Clean insignificant");
            int group = Undo.GetCurrentGroup();
            int totalRemoved = 0;

            try
            {
                foreach (var level in chain)
                {
                    if (level.IsSceneInstance) continue;
                    if (level.Root == null) continue;

                    var mods = PrefabUtility.GetPropertyModifications(level.Root);
                    if (mods == null) continue;

                    var source = PrefabUtility.GetCorrespondingObjectFromSource(level.Root);
                    if (source == null) continue;

                    var keep = new List<PropertyModification>();
                    int removed = 0;

                    foreach (var mod in mods)
                    {
                        if (mod.target == null || PrefabUtility.IsDefaultOverride(mod))
                        {
                            keep.Add(mod);
                            continue;
                        }

                        // Find corresponding property on source to compare values
                        var sourceObj = FindSourceObject(mod.target, source);
                        if (sourceObj == null)
                        {
                            keep.Add(mod);
                            continue;
                        }

                        string sourceValue = GetSourcePropertyValue(sourceObj, mod.propertyPath);
                        if (sourceValue == null)
                        {
                            keep.Add(mod);
                            continue;
                        }

                        var comparer = ComparerRouter.GetComparer(mod.propertyPath);
                        if (comparer.AreEffectivelyEqual(mod.value, sourceValue))
                        {
                            removed++;
                        }
                        else
                        {
                            keep.Add(mod);
                        }
                    }

                    if (removed > 0)
                    {
                        Undo.RecordObject(level.Root, "Clean insignificant");
                        PrefabUtility.SetPropertyModifications(level.Root, keep.ToArray());
                        totalRemoved += removed;
                    }
                }

                return totalRemoved;
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
        }

        /// <summary>
        /// Batch revert: remove every override in <paramref name="tasks"/>.
        /// Each task pairs a PrefabInstance root with one conflict that was
        /// originally discovered through that root. This overload is the
        /// correct entry point for hierarchy-mode callers, where different
        /// conflicts in the same batch can live under different nested
        /// PrefabInstance owners and therefore need different chains.
        /// Chains are cached per unique instance root so a big batch still
        /// only walks each owner once. All removals collapse into one Undo
        /// group.
        /// </summary>
        public static void BatchRevert(
            IEnumerable<(GameObject instanceRoot, PropertyConflict conflict)> tasks)
        {
            if (tasks == null) return;

            Undo.SetCurrentGroupName("Prefab Doctor: Batch revert");
            int group = Undo.GetCurrentGroup();
            try
            {
                var analyzer = new OverrideAnalyzer();
                var chainCache = new Dictionary<int, List<NestingLevel>>();

                // Phase 1: collect all (prefabRoot, propertyPath, targetId)
                // triples to remove, grouped by the prefab root's instance
                // ID. This avoids the O(N²) trap of calling
                // RemoveModification per conflict — each call read + filtered
                // + wrote the full PropertyModification array, so 5800
                // conflicts on one instance = 5800 read/filter/write cycles
                // on the same multi-thousand-entry array. Now we read ONCE,
                // build a HashSet of keys to remove, filter in one pass, and
                // write ONCE per prefab root.
                var toRemove = new Dictionary<int, (GameObject root,
                    HashSet<(string propertyPath, int targetId)> keys)>();

                foreach (var (instanceRoot, conflict) in tasks)
                {
                    if (instanceRoot == null || conflict == null) continue;

                    int rootId = instanceRoot.GetInstanceID();
                    if (!chainCache.TryGetValue(rootId, out var chain))
                    {
                        chain = analyzer.BuildChain(instanceRoot);
                        chainCache[rootId] = chain;
                    }

                    foreach (var entry in conflict.Overrides)
                    {
                        var level = chain.FirstOrDefault(l => l.Depth == entry.Depth);
                        if (level.Root == null) continue;

                        int levelRootId = level.Root.GetInstanceID();
                        if (!toRemove.TryGetValue(levelRootId, out var bucket))
                        {
                            bucket = (level.Root, new HashSet<(string, int)>());
                            toRemove[levelRootId] = bucket;
                        }

                        bucket.keys.Add((conflict.Key.PropertyPath,
                            conflict.Key.TargetInstanceId));
                    }
                }

                // Phase 2: for each unique prefab root, read mods ONCE,
                // filter out all matching keys in a single pass, write ONCE.
                foreach (var (_, (prefabRoot, keys)) in toRemove)
                {
                    var mods = PrefabUtility.GetPropertyModifications(prefabRoot);
                    if (mods == null || mods.Length == 0) continue;

                    var filtered = mods.Where(m =>
                        !(m.target != null &&
                          keys.Contains((m.propertyPath,
                              m.target.GetInstanceID())))).ToArray();

                    if (filtered.Length < mods.Length)
                    {
                        Undo.RecordObject(prefabRoot, "Batch revert");
                        PrefabUtility.SetPropertyModifications(prefabRoot, filtered);
                    }
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
        }

        /// <summary>
        /// Backward-compat wrapper for instance-analysis callers where the
        /// analyzed root is shared across every conflict in the batch.
        /// Thin shim over the tasks-based overload.
        /// </summary>
        public static void BatchRevert(GameObject root, IEnumerable<PropertyConflict> conflicts)
        {
            if (root == null || conflicts == null) return;
            BatchRevert(conflicts.Select(c => (root, c)));
        }

        // ── Internal helpers ───────────────────────────────────────

        /// <summary>
        /// Strip a single PropertyModification from <paramref name="prefabRoot"/>.
        /// Matches on (propertyPath, target InstanceID) so sibling components
        /// of the same type (e.g. two FishNet NetworkBehaviours reporting the
        /// same <c>GetType().Name</c>) cannot be clobbered by a revert of just
        /// one of them. Conflicts whose <see cref="PropertyKey.TargetInstanceId"/>
        /// is 0 (orphans, quaternion synthetic groups) intentionally hit no
        /// mods here — orphans are handled by <see cref="CleanOrphans"/>,
        /// quaternion groups are a display-only aggregation.
        /// </summary>
        private static void RemoveModification(GameObject prefabRoot, in PropertyKey key)
        {
            var mods = PrefabUtility.GetPropertyModifications(prefabRoot);
            if (mods == null) return;

            int targetId = key.TargetInstanceId;
            string propertyPath = key.PropertyPath;

            var filtered = mods.Where(m =>
                !(m.propertyPath == propertyPath &&
                  m.target != null &&
                  m.target.GetInstanceID() == targetId)).ToArray();

            if (filtered.Length < mods.Length)
            {
                Undo.RecordObject(prefabRoot, "Remove override");
                PrefabUtility.SetPropertyModifications(prefabRoot, filtered);
            }
        }

        private static Object FindSourceObject(Object instanceObj, GameObject sourceRoot)
        {
            // Try direct correspondence
            return PrefabUtility.GetCorrespondingObjectFromSource(instanceObj);
        }

        private static string GetSourcePropertyValue(Object sourceObj, string propertyPath)
        {
            // SerializedObject owns a native handle; Dispose (via using) ensures
            // it is released immediately instead of waiting for finalization.
            using var so = new SerializedObject(sourceObj);
            var prop = so.FindProperty(propertyPath);
            if (prop == null) return null;

            return prop.propertyType switch
            {
                SerializedPropertyType.Float => prop.floatValue.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                SerializedPropertyType.Integer => prop.intValue.ToString(),
                SerializedPropertyType.Boolean => prop.boolValue ? "1" : "0",
                SerializedPropertyType.String => prop.stringValue,
                SerializedPropertyType.Enum => prop.enumValueIndex.ToString(),
                SerializedPropertyType.Color => prop.colorValue.ToString(),
                SerializedPropertyType.Vector2 => prop.vector2Value.ToString(),
                SerializedPropertyType.Vector3 => prop.vector3Value.ToString(),
                SerializedPropertyType.Vector4 => prop.vector4Value.ToString(),
                _ => null
            };
        }
    }
}
