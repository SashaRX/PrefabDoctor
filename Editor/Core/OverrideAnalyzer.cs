using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Core analysis engine. Walks the nesting chain, collects overrides,
    /// detects conflicts and ping-pong patterns.
    ///
    /// Supports three modes:
    /// - Full: Analyze() — everything at once
    /// - Scoped: Analyze(subtreeRoot) — only a branch
    /// - Incremental: AnalyzeIncremental() — yields after N items per frame
    /// </summary>
    internal class OverrideAnalyzer
    {
        // ── Configuration ──────────────────────────────────────────
        public bool IncludeDefaultOverrides = false;
        public bool IncludeSceneOverrides = false;
        public bool IncludeInternalProperties = false;

        private static readonly string[] s_InternalPrefixes =
        {
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_Father",
            "m_Children",
            "m_GameObject",
            "m_Script",
            "serializedVersion"
        };

        /// <summary>
        /// Cache of SerializedObject instances — avoids repeated creation.
        /// Disposed after each analysis run and when the owning window closes.
        /// </summary>
        private readonly Dictionary<Object, SerializedObject> _soCache = new();

        // Per-run snapshot of ignore rules — rebuilt at the top of each public
        // entry point so the hot loop does not re-read settings per mod and
        // does not have to concatenate static + dynamic prefix arrays on the
        // fly. Lifetime matches _soCache: reset in each finally block.
        private string[] _runIgnoredPrefixes;
        private string[] _runIgnoredTypes;

        // Per-run lookup caches. Share lifetime with _soCache so they get
        // dropped in EndRun(). Avoid repeatedly walking Transform children or
        // GetComponents() when many overrides target the same GO/component.
        private readonly Dictionary<(int rootId, string path), GameObject> _goPathCache = new();
        private readonly Dictionary<(int goId, string typeName), Object> _componentCache = new();

        private void BeginRun()
        {
            var settings = PrefabDoctorSettings.GetOrCreateDefault();
            var extra = settings?.AdditionalIgnoredPrefixes ?? Array.Empty<string>();

            if (extra.Length == 0)
            {
                _runIgnoredPrefixes = s_InternalPrefixes;
            }
            else
            {
                _runIgnoredPrefixes = new string[s_InternalPrefixes.Length + extra.Length];
                Array.Copy(s_InternalPrefixes, 0, _runIgnoredPrefixes, 0, s_InternalPrefixes.Length);
                Array.Copy(extra, 0, _runIgnoredPrefixes, s_InternalPrefixes.Length, extra.Length);
            }

            _runIgnoredTypes = settings?.IgnoredComponentTypes ?? Array.Empty<string>();
        }

        /// <summary>
        /// Dispose every cached SerializedObject and empty the cache. Safe to
        /// call multiple times; safe to call when nothing is cached. Use from
        /// analysis finally blocks and from the window's OnDisable.
        /// </summary>
        public void ClearSerializedObjectCache()
        {
            if (_soCache.Count == 0) return;
            foreach (var so in _soCache.Values)
                so?.Dispose();
            _soCache.Clear();
        }

        private void EndRun()
        {
            ClearSerializedObjectCache();
            _runIgnoredPrefixes = null;
            _runIgnoredTypes = null;
            _goPathCache.Clear();
            _componentCache.Clear();
        }

        // ── Chain Building ─────────────────────────────────────────

        public List<NestingLevel> BuildChain(GameObject root)
        {
            var chain = new List<NestingLevel>();
            var current = root;
            int depth = 0;
            var visited = new HashSet<int>();

            while (current != null)
            {
                int id = current.GetInstanceID();
                if (visited.Contains(id)) break;
                visited.Add(id);

                string path = AssetDatabase.GetAssetPath(current);
                bool isScene = string.IsNullOrEmpty(path) ||
                               path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);

                chain.Add(new NestingLevel
                {
                    Depth = depth,
                    Root = current,
                    AssetPath = path,
                    IsSceneInstance = isScene
                });

                var source = PrefabUtility.GetCorrespondingObjectFromSource(current);
                if (source == null || source == current) break;
                current = source;
                depth++;
            }

            return chain;
        }

        // ── Override Collection ────────────────────────────────────

        public Dictionary<PropertyKey, List<OverrideEntry>> CollectOverrides(
            List<NestingLevel> chain, Transform subtreeRoot = null)
        {
            var map = new Dictionary<PropertyKey, List<OverrideEntry>>();

            for (int i = 0; i < chain.Count; i++)
            {
                var level = chain[i];

                if (level.IsSceneInstance && !IncludeSceneOverrides)
                    continue;

                PropertyModification[] mods = PrefabUtility.GetPropertyModifications(level.Root);
                if (mods == null) continue;

                foreach (var mod in mods)
                {
                    if (!IncludeDefaultOverrides && PrefabUtility.IsDefaultOverride(mod))
                        continue;

                    if (mod.target == null)
                    {
                        var orphanKey = new PropertyKey
                        {
                            ComponentType = "MISSING",
                            GameObjectPath = "?",
                            PropertyPath = mod.propertyPath
                        };
                        AddToMap(map, orphanKey, level, mod);
                        continue;
                    }

                    if (!IncludeInternalProperties && IsInternalProperty(mod.propertyPath))
                        continue;

                    // Skip ignored component types
                    if (mod.target != null && IsIgnoredComponentType(mod.target.GetType().Name))
                        continue;

                    if (subtreeRoot != null)
                    {
                        var targetGO = GetGameObject(mod.target);
                        if (targetGO != null && !targetGO.transform.IsChildOf(subtreeRoot))
                            continue;
                    }

                    var key = MakeKey(mod);
                    AddToMap(map, key, level, mod);
                }
            }

            return map;
        }

        private static void AddToMap(Dictionary<PropertyKey, List<OverrideEntry>> map,
            PropertyKey key, NestingLevel level, PropertyModification mod)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<OverrideEntry>(4);
                map[key] = list;
            }

            list.Add(new OverrideEntry
            {
                Depth = level.Depth,
                Value = mod.value,
                AssetPath = level.AssetPath,
                ObjectReference = mod.objectReference
            });
        }

        // ── Full Analysis ──────────────────────────────────────────

        public AnalysisReport Analyze(GameObject root, Transform subtreeRoot = null)
        {
            var sw = Stopwatch.StartNew();
            var report = new AnalysisReport { AnalyzedRoot = root };

            BeginRun();
            try
            {
                report.Chain = BuildChain(root);
                if (report.Chain.Count < 2)
                {
                    report.IsComplete = true;
                    report.AnalysisTimeMs = sw.ElapsedMilliseconds;
                    return report;
                }

                var overrideMap = CollectOverrides(report.Chain, subtreeRoot);
                var quaternionGroups = GroupQuaternionOverrides(overrideMap);
                var goReports = new Dictionary<string, GameObjectReport>();

                // Classify scalar properties
                foreach (var kvp in overrideMap)
                {
                    if (ComparerRouter.IsQuaternionComponent(kvp.Key.PropertyPath))
                        continue;

                    var conflict = ClassifyConflict(kvp.Key, kvp.Value, report.Chain);
                    if (conflict != null)
                        AddConflictToReport(goReports, conflict, report);
                }

                // Classify quaternion groups
                foreach (var qg in quaternionGroups)
                {
                    var conflict = ClassifyQuaternionGroup(qg);
                    if (conflict != null)
                        AddConflictToReport(goReports, conflict, report);
                }

                report.GameObjects = goReports.Values
                    .OrderByDescending(g => g.PingPongCount)
                    .ThenByDescending(g => g.MultiOverrideCount)
                    .ToList();

                report.IsComplete = true;
            }
            finally
            {
                EndRun();
                report.AnalysisTimeMs = sw.ElapsedMilliseconds;
            }

            return report;
        }

        // ── Hierarchy Analysis ─────────────────────────────────────

        /// <summary>
        /// Recursively analyzes ALL PrefabInstance roots in the hierarchy.
        /// For each nested instance, builds its own chain and collects overrides.
        /// Results are merged into a single report with full hierarchy paths.
        /// This is the "full picture" mode — shows every override at every level
        /// for every nested prefab in the entire tree.
        /// </summary>
        public AnalysisReport AnalyzeHierarchy(GameObject root)
        {
            var sw = Stopwatch.StartNew();
            var report = new AnalysisReport
            {
                AnalyzedRoot = root,
                IsHierarchyMode = true
            };

            BeginRun();
            try
            {
                // Find all PrefabInstance roots recursively
                var instanceRoots = new List<(GameObject go, string hierarchyPath)>();
                CollectPrefabInstanceRoots(root.transform, "", instanceRoots);

                report.InstancesAnalyzed = instanceRoots.Count;

                // Also include the root itself if it's a prefab instance
                if (PrefabUtility.IsPartOfPrefabInstance(root))
                {
                    var outerRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(root);
                    if (outerRoot == root)
                        instanceRoots.Insert(0, (root, root.name));
                }

                // Build a combined chain from the root (for display)
                report.Chain = BuildChain(root);

                var goReports = new Dictionary<string, GameObjectReport>();

                foreach (var (instanceGO, hierPath) in instanceRoots)
                {
                    // Build chain for this specific instance
                    var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(instanceGO);
                    if (instanceRoot == null) instanceRoot = instanceGO;

                    var chain = BuildChain(instanceRoot);
                    if (chain.Count < 2) continue;

                    // Collect overrides for this instance
                    var overrideMap = CollectOverrides(chain);

                    // Classify and merge into report — prefix paths with hierarchy location
                    foreach (var kvp in overrideMap)
                    {
                        if (ComparerRouter.IsQuaternionComponent(kvp.Key.PropertyPath))
                            continue;

                        // Prefix the GO path with hierarchy path so we know WHERE in the tree
                        var prefixedKey = new PropertyKey
                        {
                            ComponentType = kvp.Key.ComponentType,
                            GameObjectPath = hierPath.Length > 0
                                ? $"{hierPath}/{kvp.Key.GameObjectPath}"
                                : kvp.Key.GameObjectPath,
                            PropertyPath = kvp.Key.PropertyPath
                        };

                        var conflict = ClassifyConflict(prefixedKey, kvp.Value, chain);
                        if (conflict != null)
                            AddConflictToReport(goReports, conflict, report);
                    }

                    // Quaternion groups
                    var qGroups = GroupQuaternionOverrides(overrideMap);
                    foreach (var qg in qGroups)
                    {
                        var prefixedKey = new PropertyKey
                        {
                            ComponentType = qg.BaseKey.ComponentType,
                            GameObjectPath = hierPath.Length > 0
                                ? $"{hierPath}/{qg.BaseKey.GameObjectPath}"
                                : qg.BaseKey.GameObjectPath,
                            PropertyPath = qg.BaseKey.PropertyPath
                        };

                        var prefixedQg = new QuaternionGroup
                        {
                            BaseKey = prefixedKey,
                            ValuesByDepth = qg.ValuesByDepth,
                            AssetPathsByDepth = qg.AssetPathsByDepth
                        };

                        var conflict = ClassifyQuaternionGroup(prefixedQg);
                        if (conflict != null)
                            AddConflictToReport(goReports, conflict, report);
                    }
                }

                report.GameObjects = goReports.Values
                    .OrderByDescending(g => g.PingPongCount)
                    .ThenByDescending(g => g.MultiOverrideCount)
                    .ThenByDescending(g => g.InsignificantCount)
                    .ToList();

                report.IsComplete = true;
            }
            finally
            {
                EndRun();
                report.AnalysisTimeMs = sw.ElapsedMilliseconds;
            }

            return report;
        }

        /// <summary>
        /// Recursively find all GameObjects that are PrefabInstance roots.
        /// </summary>
        private void CollectPrefabInstanceRoots(Transform parent, string parentPath,
            List<(GameObject go, string hierarchyPath)> results)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                string childPath = parentPath.Length > 0
                    ? $"{parentPath}/{child.name}"
                    : child.name;

                if (PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                {
                    results.Add((child.gameObject, childPath));
                }

                // Continue recursing into children — nested instances inside nested instances
                CollectPrefabInstanceRoots(child, childPath, results);
            }
        }

        /// <summary>
        /// Incremental analysis for large prefabs.
        /// Use with EditorCoroutine or manual pump via EditorApplication.delayCall.
        /// Yields progress [0..1].
        /// </summary>
        public IEnumerator<float> AnalyzeIncremental(
            GameObject root, AnalysisReport report, int batchSize = 500)
        {
            var sw = Stopwatch.StartNew();
            BeginRun();
            report.AnalyzedRoot = root;
            report.Chain = BuildChain(root);

            if (report.Chain.Count < 2)
            {
                report.IsComplete = true;
                report.AnalysisTimeMs = sw.ElapsedMilliseconds;
                yield break;
            }

            var overrideMap = CollectOverrides(report.Chain);
            int totalKeys = overrideMap.Count;
            int processed = 0;

            var quaternionGroups = GroupQuaternionOverrides(overrideMap);
            var goReports = new Dictionary<string, GameObjectReport>();

            foreach (var kvp in overrideMap)
            {
                if (ComparerRouter.IsQuaternionComponent(kvp.Key.PropertyPath))
                {
                    processed++;
                    continue;
                }

                var conflict = ClassifyConflict(kvp.Key, kvp.Value, report.Chain);
                if (conflict != null)
                    AddConflictToReport(goReports, conflict, report);

                processed++;
                if (processed % batchSize == 0)
                    yield return (float)processed / totalKeys;
            }

            foreach (var qg in quaternionGroups)
            {
                var conflict = ClassifyQuaternionGroup(qg);
                if (conflict != null)
                    AddConflictToReport(goReports, conflict, report);
            }

            report.GameObjects = goReports.Values
                .OrderByDescending(g => g.PingPongCount)
                .ThenByDescending(g => g.MultiOverrideCount)
                .ToList();

            report.IsComplete = true;
            report.AnalysisTimeMs = sw.ElapsedMilliseconds;
            EndRun();

            yield return 1f;
        }

        // ── Quaternion Grouping ────────────────────────────────────

        private struct QuaternionGroup
        {
            public PropertyKey BaseKey;
            public Dictionary<int, Quaternion> ValuesByDepth;
            public Dictionary<int, string> AssetPathsByDepth;
        }

        private List<QuaternionGroup> GroupQuaternionOverrides(
            Dictionary<PropertyKey, List<OverrideEntry>> map)
        {
            // (goPath, compType) → depth → float[4]{x,y,z,w}
            var collector = new Dictionary<(string goPath, string compType),
                Dictionary<int, float[]>>();
            var paths = new Dictionary<(string goPath, string compType),
                Dictionary<int, string>>();

            foreach (var kvp in map)
            {
                if (!ComparerRouter.IsQuaternionComponent(kvp.Key.PropertyPath))
                    continue;

                var (_, suffix) = ComparerRouter.SplitQuaternionPath(kvp.Key.PropertyPath);
                int idx = suffix switch { ".x" => 0, ".y" => 1, ".z" => 2, ".w" => 3, _ => -1 };
                if (idx < 0) continue;

                var gk = (kvp.Key.GameObjectPath, kvp.Key.ComponentType);

                if (!collector.TryGetValue(gk, out var depthMap))
                {
                    depthMap = new Dictionary<int, float[]>();
                    collector[gk] = depthMap;
                    paths[gk] = new Dictionary<int, string>();
                }

                foreach (var entry in kvp.Value)
                {
                    if (!depthMap.TryGetValue(entry.Depth, out var comp))
                    {
                        comp = new float[4];
                        depthMap[entry.Depth] = comp;
                    }

                    if (float.TryParse(entry.Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float val))
                        comp[idx] = val;

                    paths[gk][entry.Depth] = entry.AssetPath;
                }
            }

            var results = new List<QuaternionGroup>();
            foreach (var kvp in collector)
            {
                var qg = new QuaternionGroup
                {
                    BaseKey = new PropertyKey
                    {
                        ComponentType = kvp.Key.compType,
                        GameObjectPath = kvp.Key.goPath,
                        PropertyPath = "m_LocalRotation [Q]"
                    },
                    ValuesByDepth = new Dictionary<int, Quaternion>(),
                    AssetPathsByDepth = paths[kvp.Key]
                };

                foreach (var (depth, c) in kvp.Value)
                {
                    var q = new Quaternion(c[0], c[1], c[2], c[3]);
                    if (q.w < 0) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
                    qg.ValuesByDepth[depth] = q;
                }

                if (qg.ValuesByDepth.Count >= 2)
                    results.Add(qg);
            }

            return results;
        }

        private PropertyConflict ClassifyQuaternionGroup(QuaternionGroup qg)
        {
            const float dotThreshold = 0.99999f; // ~0.01°

            var depths = qg.ValuesByDepth.Keys.OrderBy(d => d).ToList();
            if (depths.Count < 2) return null;

            // All same rotation?
            bool allSame = true;
            var first = qg.ValuesByDepth[depths[0]];
            for (int i = 1; i < depths.Count; i++)
            {
                if (Mathf.Abs(Quaternion.Dot(first, qg.ValuesByDepth[depths[i]])) < dotThreshold)
                { allSame = false; break; }
            }

            var entries = depths.Select(d => new OverrideEntry
            {
                Depth = d,
                Value = FmtQ(qg.ValuesByDepth[d]),
                AssetPath = qg.AssetPathsByDepth.GetValueOrDefault(d, "")
            }).ToList();

            if (allSame)
                return new PropertyConflict
                {
                    Key = qg.BaseKey,
                    Severity = ConflictSeverity.Insignificant,
                    Category = CategorizeProperty(qg.BaseKey.PropertyPath),
                    Overrides = entries
                };

            // Ping-pong check
            for (int i = 0; i < depths.Count; i++)
            {
                var qi = qg.ValuesByDepth[depths[i]];
                for (int j = i + 2; j < depths.Count; j++)
                {
                    if (Mathf.Abs(Quaternion.Dot(qi, qg.ValuesByDepth[depths[j]])) < dotThreshold)
                        continue;
                    for (int k = i + 1; k < j; k++)
                    {
                        if (Mathf.Abs(Quaternion.Dot(qi, qg.ValuesByDepth[depths[k]])) < dotThreshold)
                            return new PropertyConflict
                            {
                                Key = qg.BaseKey,
                                Severity = ConflictSeverity.PingPong,
                                Category = CategorizeProperty(qg.BaseKey.PropertyPath),
                                Overrides = entries,
                                PingPongIndices = (i, k, j)
                            };
                    }
                }
            }

            return new PropertyConflict
            {
                Key = qg.BaseKey,
                Severity = ConflictSeverity.MultiOverride,
                Category = CategorizeProperty(qg.BaseKey.PropertyPath),
                Overrides = entries
            };
        }

        private static string FmtQ(Quaternion q) =>
            $"({q.x:F4}, {q.y:F4}, {q.z:F4}, {q.w:F4})";

        // ── Conflict Classification ────────────────────────────────

        /// <summary>
        /// Group a property by the kind of data it holds, independently of how
        /// severe the override is. Used by the UI for per-category filtering
        /// and status bar summaries.
        /// </summary>
        internal static OverrideCategory CategorizeProperty(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return OverrideCategory.General;

            // Lightmap noise
            if (propertyPath == "m_ScaleInLightmap"
                || propertyPath == "m_LightmapIndex"
                || propertyPath.StartsWith("m_LightmapTilingOffset"))
                return OverrideCategory.Lightmap;

            // Network noise (Netcode for GameObjects and similar)
            if (propertyPath == "WasActiveDuringEdit"
                || propertyPath == "_initializedTimestamp"
                || propertyPath == "_networkObjectCache"
                || propertyPath.Contains("k__BackingField")
                || propertyPath.StartsWith("NetworkBehaviours"))
                return OverrideCategory.NetworkNoise;

            if (propertyPath == "m_StaticEditorFlags") return OverrideCategory.StaticFlags;
            if (propertyPath == "m_Name")              return OverrideCategory.Name;

            if (propertyPath.StartsWith("m_LocalPosition")
                || propertyPath.StartsWith("m_LocalRotation")
                || propertyPath.StartsWith("m_LocalScale")
                || propertyPath.StartsWith("m_LocalEulerAnglesHint"))
                return OverrideCategory.Transform;

            return OverrideCategory.General;
        }

        private PropertyConflict ClassifyConflict(PropertyKey key,
            List<OverrideEntry> entries, List<NestingLevel> chain)
        {
            var conflict = new PropertyConflict
            {
                Key = key,
                Overrides = entries,
                Category = CategorizeProperty(key.PropertyPath)
            };

            if (key.ComponentType == "MISSING")
            {
                conflict.Severity = ConflictSeverity.Orphan;
                return conflict;
            }

            if (entries.Count == 1)
            {
                if (CheckInsignificant(key, entries[0], chain))
                {
                    conflict.Severity = ConflictSeverity.Insignificant;
                    return conflict;
                }
                return null; // single real override — not a conflict
            }

            // 2+ overrides
            var pingPong = DetectPingPong(entries, key.PropertyPath);
            if (pingPong.HasValue)
            {
                conflict.Severity = ConflictSeverity.PingPong;
                conflict.PingPongIndices = pingPong.Value;
            }
            else
            {
                conflict.Severity = AreAllOverridesEffectivelySame(entries, key.PropertyPath)
                    ? ConflictSeverity.Insignificant
                    : ConflictSeverity.MultiOverride;
            }

            return conflict;
        }

        // ── Ping-Pong Detection ────────────────────────────────────

        private (int first, int middle, int pingBack)? DetectPingPong(
            List<OverrideEntry> entries, string propertyPath)
        {
            int n = entries.Count;
            if (n < 2) return null;

            // Build an index array sorted by depth so we can walk entries in
            // depth order without allocating a second list. Insertion sort is
            // fine: n is almost always 2-5 (one override per nesting level).
            // Returning original indices removes the O(n) List.IndexOf scans
            // the old implementation did on the success path.
            Span<int> order = n <= 16 ? stackalloc int[n] : new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            for (int i = 1; i < n; i++)
            {
                int cur = order[i];
                int curDepth = entries[cur].Depth;
                int j = i - 1;
                while (j >= 0 && entries[order[j]].Depth > curDepth)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = cur;
            }

            var comparer = ComparerRouter.GetComparer(propertyPath);

            for (int i = 0; i < n; i++)
            {
                int idxI = order[i];
                string valI = entries[idxI].Value;

                for (int j = i + 2; j < n; j++)
                {
                    int idxJ = order[j];
                    if (!comparer.AreEffectivelyEqual(valI, entries[idxJ].Value))
                        continue;

                    for (int k = i + 1; k < j; k++)
                    {
                        int idxK = order[k];
                        if (!comparer.AreEffectivelyEqual(entries[idxK].Value, valI))
                            return (idxI, idxK, idxJ);
                    }
                }
            }

            return null;
        }

        private bool AreAllOverridesEffectivelySame(List<OverrideEntry> entries, string propertyPath)
        {
            if (entries.Count < 2) return false;
            var comparer = ComparerRouter.GetComparer(propertyPath);
            for (int i = 1; i < entries.Count; i++)
            {
                if (!comparer.AreEffectivelyEqual(entries[0].Value, entries[i].Value))
                    return false;
            }
            return true;
        }

        // ── Insignificant Override Detection ───────────────────────

        /// <summary>
        /// Real implementation: reads source property value via SerializedObject
        /// and compares with epsilon-aware comparer.
        /// </summary>
        private bool CheckInsignificant(PropertyKey key, OverrideEntry entry,
            List<NestingLevel> chain)
        {
            int sourceDepth = entry.Depth + 1;
            var sourceLevel = chain.FirstOrDefault(l => l.Depth == sourceDepth);
            if (sourceLevel.Root == null) return false;

            var sourceGO = GetGameObjectByPathCached(sourceLevel.Root, key.GameObjectPath);
            if (sourceGO == null) return false;

            var sourceObj = GetComponentCached(sourceGO, key.ComponentType);
            if (sourceObj == null) return false;

            string sourceValue = ReadPropertyValue(sourceObj, key.PropertyPath);
            if (sourceValue == null) return false;

            var comparer = ComparerRouter.GetComparer(key.PropertyPath);
            return comparer.AreEffectivelyEqual(entry.Value, sourceValue);
        }

        private GameObject GetGameObjectByPathCached(GameObject root, string relativePath)
        {
            var cacheKey = (root.GetInstanceID(), relativePath);
            if (_goPathCache.TryGetValue(cacheKey, out var cached)) return cached;
            var go = FindGameObjectByPath(root, relativePath);
            _goPathCache[cacheKey] = go;
            return go;
        }

        private Object GetComponentCached(GameObject go, string typeName)
        {
            var cacheKey = (go.GetInstanceID(), typeName);
            if (_componentCache.TryGetValue(cacheKey, out var cached)) return cached;
            var obj = FindComponent(go, typeName);
            _componentCache[cacheKey] = obj;
            return obj;
        }

        /// <summary>
        /// Read property value as string from Object, with SO caching.
        /// </summary>
        private string ReadPropertyValue(Object obj, string propertyPath)
        {
            if (!_soCache.TryGetValue(obj, out var so))
            {
                so = new SerializedObject(obj);
                _soCache[obj] = so;
            }

            var prop = so.FindProperty(propertyPath);
            if (prop == null) return null;

            return prop.propertyType switch
            {
                SerializedPropertyType.Float =>
                    prop.floatValue.ToString(CultureInfo.InvariantCulture),
                SerializedPropertyType.Integer =>
                    prop.intValue.ToString(),
                SerializedPropertyType.Boolean =>
                    prop.boolValue ? "1" : "0",
                SerializedPropertyType.String =>
                    prop.stringValue,
                SerializedPropertyType.Enum =>
                    prop.enumValueIndex.ToString(),
                SerializedPropertyType.ObjectReference =>
                    prop.objectReferenceValue != null
                        ? prop.objectReferenceValue.GetInstanceID().ToString()
                        : "0",
                SerializedPropertyType.LayerMask =>
                    prop.intValue.ToString(),
                _ => null
            };
        }

        // ── GameObject / Component lookup ──────────────────────────

        private static GameObject FindGameObjectByPath(GameObject root, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath == "?") return null;

            string[] parts = relativePath.Split('/');
            if (parts.Length == 0) return null;
            if (parts[0] != root.name) return null;
            if (parts.Length == 1) return root;

            Transform current = root.transform;
            for (int i = 1; i < parts.Length; i++)
            {
                Transform child = current.Find(parts[i]);
                if (child == null) return null;
                current = child;
            }
            return current.gameObject;
        }

        private static Object FindComponent(GameObject go, string typeName)
        {
            if (typeName == "GameObject") return go;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                if (comp.GetType().Name == typeName) return comp;
            }
            return null;
        }

        // ── Report helpers ─────────────────────────────────────────

        private static void AddConflictToReport(
            Dictionary<string, GameObjectReport> goReports,
            PropertyConflict conflict, AnalysisReport report)
        {
            string goPath = conflict.Key.GameObjectPath;
            if (!goReports.TryGetValue(goPath, out var goReport))
            {
                goReport = new GameObjectReport { RelativePath = goPath };
                goReports[goPath] = goReport;
            }

            goReport.Conflicts.Add(conflict);

            switch (conflict.Severity)
            {
                case ConflictSeverity.PingPong:
                    goReport.PingPongCount++;
                    report.TotalPingPong++;
                    break;
                case ConflictSeverity.MultiOverride:
                    goReport.MultiOverrideCount++;
                    report.TotalMultiOverride++;
                    break;
                case ConflictSeverity.Insignificant:
                    goReport.InsignificantCount++;
                    report.TotalInsignificant++;
                    break;
                case ConflictSeverity.Orphan:
                    goReport.OrphanCount++;
                    report.TotalOrphan++;
                    break;
            }
        }

        // ── Generic helpers ────────────────────────────────────────

        private static PropertyKey MakeKey(PropertyModification mod)
        {
            return new PropertyKey
            {
                ComponentType = mod.target != null ? mod.target.GetType().Name : "Unknown",
                GameObjectPath = GetRelativePath(mod.target),
                PropertyPath = mod.propertyPath
            };
        }

        private static string GetRelativePath(Object target)
        {
            var go = GetGameObject(target);
            if (go == null) return "?";

            var parts = new List<string>();
            var current = go.transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static GameObject GetGameObject(Object obj)
        {
            if (obj is GameObject go) return go;
            if (obj is Component comp) return comp.gameObject;
            return null;
        }

        private bool IsInternalProperty(string propertyPath)
        {
            // _runIgnoredPrefixes is the merged static + settings list, built
            // once per run in BeginRun(). If a caller forgot to BeginRun, fall
            // back to the static list so we stay correct.
            var prefixes = _runIgnoredPrefixes ?? s_InternalPrefixes;
            foreach (var prefix in prefixes)
            {
                if (!string.IsNullOrEmpty(prefix) &&
                    propertyPath.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private bool IsIgnoredComponentType(string typeName)
        {
            var types = _runIgnoredTypes;
            if (types == null || types.Length == 0) return false;

            foreach (var ignored in types)
            {
                if (!string.IsNullOrEmpty(ignored) && typeName == ignored)
                    return true;
            }
            return false;
        }
    }
}
