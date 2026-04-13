using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
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

        /// <summary>
        /// When true, <see cref="ClassifyConflict"/> skips the expensive
        /// <see cref="CheckInsignificant"/> path (which opens a
        /// SerializedObject to compare values with epsilon). All
        /// single-depth overrides are classified as Insignificant
        /// directly. Actionable counts (PingPong, MultiOverride, Orphan)
        /// are unaffected. Set automatically in hierarchy mode.
        /// </summary>
        public bool FastClassify = false;

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

        // Processed-mods cache for hierarchy mode: keyed by the instance ID
        // of a depth-1+ chain Root (a prefab asset GO), stores the already-
        // filtered and keyed override entries so CollectOverridesFast can
        // merge them into the per-instance map with a cheap loop instead of
        // re-running GetPropertyModifications + ProcessMod for every
        // instance of the same prefab.
        private readonly Dictionary<int, List<(PropertyKey key, OverrideEntry entry)>>
            _processedModsCache = new();

        // Key base cache: MakeKey does GetType().Name (reflection) and
        // GetRelativePath (walks the whole transform parent chain + string
        // concat) per mod. All mods targeting the same component share
        // the same (ComponentType, GameObjectPath, TargetInstanceId) — only
        // PropertyPath differs. Caching the base by target InstanceID turns
        // 22M reflection+walk calls into ~5000 on the user's scene.
        private readonly Dictionary<int, (string componentType, string goPath, int targetId)>
            _keyBaseCache = new();

        /// <summary>
        /// Monotonically increasing run ID — incremented in BeginRun().
        /// Incremental jobs capture this at start; if AbortRun() causes a
        /// new BeginRun(), the old job detects the mismatch and yields break.
        /// </summary>
        private int _runId;

        private void BeginRun()
        {
            _runId++;
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
            _processedModsCache.Clear();
            _keyBaseCache.Clear();
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

        /// <summary>
        /// Build a chain for <paramref name="instanceRoot"/>, reusing a
        /// cached template for depth 1+ if one exists. Many scene instances
        /// share the same prefab asset, so their chains are structurally
        /// identical from depth 1 onward — only depth 0 (the scene
        /// instance itself) differs. Caching saves
        /// O(chain_depth × Unity_API_call) per duplicate instance.
        /// </summary>
        private List<NestingLevel> BuildChainCached(
            GameObject instanceRoot,
            Dictionary<string, List<NestingLevel>> templateCache)
        {
            // Get the immediate source prefab for the cache key.
            var source = PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot);
            string cacheKey = source != null ? AssetDatabase.GetAssetPath(source) : null;

            if (!string.IsNullOrEmpty(cacheKey)
                && templateCache.TryGetValue(cacheKey, out var template))
            {
                // Template hit: build depth 0 from the instance, append
                // the cached depth 1+ levels unchanged.
                string rootPath = AssetDatabase.GetAssetPath(instanceRoot);
                bool isScene = string.IsNullOrEmpty(rootPath)
                    || rootPath.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase);

                var chain = new List<NestingLevel>(template.Count + 1);
                chain.Add(new NestingLevel
                {
                    Depth = 0,
                    Root = instanceRoot,
                    AssetPath = rootPath,
                    IsSceneInstance = isScene
                });
                chain.AddRange(template);
                return chain;
            }

            // Miss: full BuildChain, then cache depth 1+ as template.
            var fullChain = BuildChain(instanceRoot);
            if (!string.IsNullOrEmpty(cacheKey) && fullChain.Count >= 2)
            {
                var tmpl = new List<NestingLevel>(fullChain.Count - 1);
                for (int t = 1; t < fullChain.Count; t++)
                    tmpl.Add(fullChain[t]);
                templateCache[cacheKey] = tmpl;
            }

            return fullChain;
        }

        // ── Override Collection ────────────────────────────────────

        public Dictionary<PropertyKey, List<OverrideEntry>> CollectOverrides(
            List<NestingLevel> chain, Transform subtreeRoot = null)
        {
            var map = new Dictionary<PropertyKey, List<OverrideEntry>>();
            // Drain the incremental form to completion — this is the sync
            // wrapper that existing call sites (instance-mode analysis,
            // unit tests) rely on.
            foreach (var _ in CollectOverridesIncremental(chain, map, subtreeRoot, 0f)) { }
            return map;
        }

        /// <summary>
        /// Non-yielding override collection for hierarchy mode. Same
        /// logic as <see cref="CollectOverridesIncremental"/> but without
        /// the IEnumerator state-machine overhead and per-mod yield
        /// points. The hierarchy pump already uses a 200ms budget and
        /// yields at instance boundaries; mid-instance yields are pure
        /// overhead on scenes with millions of mods.
        ///
        /// Depth 1+ optimisation: prefab-asset chain levels are shared
        /// across every scene instance of the same prefab. Their
        /// GetPropertyModifications + ProcessMod results are cached in
        /// <see cref="_processedModsCache"/> on first encounter and then
        /// merged into the per-instance map with a cheap loop for every
        /// subsequent instance. This turns O(instances × mods_per_level)
        /// Unity API calls at depth 1+ into O(unique_prefabs × mods).
        /// </summary>
        private void CollectOverridesFast(
            List<NestingLevel> chain,
            Dictionary<PropertyKey, List<OverrideEntry>> map)
        {
            for (int i = 0; i < chain.Count; i++)
            {
                var level = chain[i];
                if (level.IsSceneInstance && !IncludeSceneOverrides) continue;

                // Depth 1+ levels point to prefab asset roots, shared across
                // all instances of the same prefab. Cache the processed
                // output once and fast-merge for subsequent instances.
                if (i > 0 && !level.IsSceneInstance)
                {
                    int rootId = level.Root.GetInstanceID();
                    if (!_processedModsCache.TryGetValue(rootId, out var cached))
                    {
                        var tempMap = new Dictionary<PropertyKey, List<OverrideEntry>>();
                        var mods = PrefabUtility.GetPropertyModifications(level.Root);
                        if (mods != null)
                        {
                            for (int m = 0; m < mods.Length; m++)
                                ProcessModFast(mods[m], level, tempMap);
                        }

                        cached = new List<(PropertyKey, OverrideEntry)>();
                        foreach (var kvp in tempMap)
                            foreach (var entry in kvp.Value)
                                cached.Add((kvp.Key, entry));

                        _processedModsCache[rootId] = cached;
                    }

                    for (int c = 0; c < cached.Count; c++)
                    {
                        var (key, entry) = cached[c];
                        if (!map.TryGetValue(key, out var list))
                        {
                            list = new List<OverrideEntry>(4);
                            map[key] = list;
                        }
                        list.Add(entry);
                    }
                }
                else
                {
                    // Depth 0 (scene instance): always unique per instance.
                    // Gate: skip the expensive GetPropertyModifications if
                    // no non-default overrides exist on this instance.
                    if (!IncludeDefaultOverrides
                        && level.Root != null
                        && !PrefabUtility.HasPrefabInstanceAnyOverrides(level.Root, false))
                        continue;

                    var mods = PrefabUtility.GetPropertyModifications(level.Root);
                    if (mods == null) continue;

                    for (int m = 0; m < mods.Length; m++)
                        ProcessModFast(mods[m], level, map);
                }
            }
        }

        /// <summary>
        /// Stripped-down ProcessMod for hierarchy mode's fast path.
        /// Two key differences from the regular <see cref="ProcessMod"/>:
        /// <list type="number">
        ///   <item>Skips <c>PrefabUtility.IsDefaultOverride</c> (~2-5μs
        ///   per call × 22M mods = ~50-110s saved). Default overrides
        ///   that slip through are classified as Insignificant and don't
        ///   affect actionable counts.</item>
        ///   <item>Uses <see cref="_keyBaseCache"/> to avoid
        ///   <c>GetType().Name</c> (reflection) and
        ///   <c>GetRelativePath</c> (transform parent walk + string
        ///   concat) for every mod. All mods targeting the same component
        ///   share the same key base — only <c>PropertyPath</c> differs.
        ///   22M lookups → ~5000 computes.</item>
        /// </list>
        /// </summary>
        private void ProcessModFast(PropertyModification mod, NestingLevel level,
            Dictionary<PropertyKey, List<OverrideEntry>> map)
        {
            // Skip default overrides early — avoids processing m_Name,
            // m_IsActive, m_Father etc. that Unity always emits.
            if (!IncludeDefaultOverrides && PrefabUtility.IsDefaultOverride(mod))
                return;

            if (mod.target == null)
            {
                var orphanKey = new PropertyKey
                {
                    ComponentType = "MISSING",
                    GameObjectPath = "(orphaned)",
                    PropertyPath = mod.propertyPath,
                    TargetInstanceId = 0
                };
                AddToMap(map, orphanKey, level, mod);
                return;
            }

            if (!IncludeInternalProperties && IsInternalProperty(mod.propertyPath))
                return;

            // Cached key base: GetType().Name + GetRelativePath + GetInstanceID
            int targetId = mod.target.GetInstanceID();
            if (!_keyBaseCache.TryGetValue(targetId, out var keyBase))
            {
                string typeName = mod.target.GetType().Name;
                keyBase = (typeName, GetRelativePath(mod.target), targetId);
                _keyBaseCache[targetId] = keyBase;
            }

            if (IsIgnoredComponentType(keyBase.componentType))
                return;

            var key = new PropertyKey
            {
                ComponentType = keyBase.componentType,
                GameObjectPath = keyBase.goPath,
                PropertyPath = mod.propertyPath,
                TargetInstanceId = keyBase.targetId
            };
            AddToMap(map, key, level, mod);
        }

        /// <summary>
        /// Incremental override collection. Fills <paramref name="map"/> in
        /// place and yields the <paramref name="progress"/> value every
        /// <c>modBudgetPerYield</c> processed modifications so the caller
        /// can keep the editor responsive even on prefab instances with
        /// thousands of property modifications (network backing fields,
        /// scene-level overrides, etc.).
        ///
        /// The yielded float is just a hint for the caller's progress bar;
        /// the real signal is "we yielded" (giving the pump a chance to
        /// breathe), not the numeric value.
        /// </summary>
        private IEnumerable<float> CollectOverridesIncremental(
            List<NestingLevel> chain,
            Dictionary<PropertyKey, List<OverrideEntry>> map,
            Transform subtreeRoot,
            float progress)
        {
            const int modBudgetPerYield = 200;
            int modsSinceYield = 0;

            for (int i = 0; i < chain.Count; i++)
            {
                var level = chain[i];

                if (level.IsSceneInstance && !IncludeSceneOverrides)
                    continue;

                PropertyModification[] mods = PrefabUtility.GetPropertyModifications(level.Root);
                if (mods == null) continue;

                foreach (var mod in mods)
                {
                    ProcessMod(mod, level, map, subtreeRoot);

                    if (++modsSinceYield >= modBudgetPerYield)
                    {
                        modsSinceYield = 0;
                        yield return progress;
                    }
                }
            }
        }

        /// <summary>
        /// Classify a single <see cref="PropertyModification"/> and add it
        /// to <paramref name="map"/>. Extracted from the old loop body so
        /// the incremental collector can reuse it without duplicating the
        /// orphan / internal / ignored-type filtering rules.
        /// </summary>
        private void ProcessMod(
            PropertyModification mod,
            NestingLevel level,
            Dictionary<PropertyKey, List<OverrideEntry>> map,
            Transform subtreeRoot)
        {
            if (!IncludeDefaultOverrides && PrefabUtility.IsDefaultOverride(mod))
                return;

            if (mod.target == null)
            {
                // Orphans from different removed components all have
                // target == null, so we cannot get a sibling-distinguishing
                // InstanceID. Leave TargetInstanceId = 0; the formatter's
                // (depth, value) run-length collapse handles the resulting
                // duplicates on the display side.
                var orphanKey = new PropertyKey
                {
                    ComponentType = "MISSING",
                    GameObjectPath = "(orphaned)",
                    PropertyPath = mod.propertyPath,
                    TargetInstanceId = 0
                };
                AddToMap(map, orphanKey, level, mod);
                return;
            }

            if (!IncludeInternalProperties && IsInternalProperty(mod.propertyPath))
                return;

            // Use the same _keyBaseCache as ProcessModFast to avoid
            // GetType().Name (reflection) and GetRelativePath (transform walk)
            // on every mod — all mods targeting the same component share
            // the same key base.
            int targetId = mod.target.GetInstanceID();
            if (!_keyBaseCache.TryGetValue(targetId, out var keyBase))
            {
                string typeName = mod.target.GetType().Name;
                keyBase = (typeName, GetRelativePath(mod.target), targetId);
                _keyBaseCache[targetId] = keyBase;
            }

            if (IsIgnoredComponentType(keyBase.componentType))
                return;

            if (subtreeRoot != null)
            {
                var targetGO = GetGameObject(mod.target);
                if (targetGO != null && !targetGO.transform.IsChildOf(subtreeRoot))
                    return;
            }

            var key = new PropertyKey
            {
                ComponentType = keyBase.componentType,
                GameObjectPath = keyBase.goPath,
                PropertyPath = mod.propertyPath,
                TargetInstanceId = keyBase.targetId
            };
            AddToMap(map, key, level, mod);
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

                // Collect dependent asset paths for the health scan.
                foreach (var level in report.Chain)
                    if (!string.IsNullOrEmpty(level.AssetPath) && !level.IsSceneInstance)
                        report.DependentAssetPaths.Add(level.AssetPath);

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

                var goList = new List<GameObjectReport>(goReports.Values);
                goList.Sort(static (a, b) =>
                {
                    int c = b.PingPongCount.CompareTo(a.PingPongCount);
                    if (c != 0) return c;
                    c = b.MultiOverrideCount.CompareTo(a.MultiOverrideCount);
                    if (c != 0) return c;
                    return b.InsignificantCount.CompareTo(a.InsignificantCount);
                });
                report.GameObjects = goList;

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
        ///
        /// Synchronous wrapper around <see cref="AnalyzeHierarchyIncremental"/>
        /// for callers that do not need cancelation or progress — internally
        /// this just pumps the enumerator to completion. The editor will
        /// freeze while it runs; prefer the incremental path from the window.
        /// </summary>
        public AnalysisReport AnalyzeHierarchy(GameObject root)
        {
            var report = new AnalysisReport();
            var job = AnalyzeHierarchyIncremental(root, report);
            while (job.MoveNext()) { /* pump to completion */ }
            return report;
        }

        /// <summary>
        /// Incremental variant of <see cref="AnalyzeHierarchy"/>. Yields a
        /// float progress in [0..1] after each analyzed PrefabInstance root,
        /// so the caller can pump it via <c>EditorApplication.update</c> and
        /// keep the editor responsive. Always yields a final 1f once the
        /// report is fully populated.
        ///
        /// Cancelation: if the caller stops pumping and invokes
        /// <see cref="AbortRun"/>, the per-run caches (SerializedObject cache,
        /// ignore snapshot, GO/component caches) are released. Without
        /// calling AbortRun on a cancelled run those caches leak for the
        /// rest of the editor session.
        /// </summary>
        public IEnumerator<float> AnalyzeHierarchyIncremental(
            GameObject root, AnalysisReport report)
        {
            var sw = Stopwatch.StartNew();
            report.AnalyzedRoot = root;
            report.IsHierarchyMode = true;
            report.AssetToInstances = new Dictionary<string, List<GameObject>>();
            report.InstanceToAsset = new Dictionary<GameObject, string>();

            BeginRun();
            int myRunId = _runId;

            // Find all PrefabInstance roots recursively.
            var instanceRoots = new List<(GameObject go, string hierarchyPath)>();
            var pathBuilder = new System.Text.StringBuilder(256);
            CollectPrefabInstanceRoots(root.transform, pathBuilder, instanceRoots);

            if (PrefabUtility.IsPartOfPrefabInstance(root))
            {
                var outerRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(root);
                if (outerRoot == root)
                    instanceRoots.Insert(0, (root, root.name));
            }

            report.InstancesAnalyzed = instanceRoots.Count;
            report.Chain = BuildChain(root);

            var goReports = new Dictionary<string, GameObjectReport>();
            var goPathToRoot = new Dictionary<string, GameObject>();
            int total = instanceRoots.Count;
            var chainTemplateCache = new Dictionary<string, List<NestingLevel>>();

            // ── Phase 1: Collect raw data on main thread (Unity API) ──
            // Batch: fetch chains + depth-0 mods for all instances.
            // Pre-populate _processedModsCache for depth 1+ levels.
            const int batchSize = 64;
            var batch = new List<(
                GameObject instanceRoot,
                string hierPath,
                List<NestingLevel> chain)>(batchSize);

            for (int idx = 0; idx < total; idx++)
            {
                var (instanceGO, hierPath) = instanceRoots[idx];
                var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(instanceGO);
                if (instanceRoot == null) instanceRoot = instanceGO;

                report.HierarchyInstanceRoots.Add(instanceRoot);

                var chain = BuildChainCached(instanceRoot, chainTemplateCache);

                // Map instance → BASE prefab asset (chain[last] = deepest source)
                // for UI grouping by prefab type. BuildChain does NOT reverse:
                // chain[0] = scene instance, chain[last] = base asset.
                var baseLevel = chain[chain.Count - 1];
                if (chain.Count >= 2 && !baseLevel.IsSceneInstance)
                {
                    string srcPath = baseLevel.AssetPath;
                    if (!string.IsNullOrEmpty(srcPath))
                    {
                        report.InstanceToAsset[instanceRoot] = srcPath;
                        if (!report.AssetToInstances.TryGetValue(srcPath, out var instList))
                        {
                            instList = new List<GameObject>();
                            report.AssetToInstances[srcPath] = instList;
                        }
                        instList.Add(instanceRoot);
                    }
                }

                foreach (var level in chain)
                    if (!string.IsNullOrEmpty(level.AssetPath) && !level.IsSceneInstance)
                        report.DependentAssetPaths.Add(level.AssetPath);

                if (chain.Count < 2) continue;

                // Gate: skip instances with no non-default overrides.
                // HasPrefabInstanceAnyOverrides is documented as the fastest
                // way to check — avoids the expensive GetPropertyModifications.
                if (!IncludeDefaultOverrides
                    && !PrefabUtility.HasPrefabInstanceAnyOverrides(instanceRoot, false))
                    continue;

                batch.Add((instanceRoot, hierPath, chain));

                // ── Phase 2: Process batch in parallel ──
                if (batch.Count >= batchSize || idx == total - 1)
                {
                    ProcessBatchParallel(batch, goReports, goPathToRoot, report);
                    batch.Clear();
                }

                if (_runId != myRunId) yield break;
                yield return total > 0 ? (float)(idx + 1) / total : 1f;
            }

            var goList = new List<GameObjectReport>(goReports.Values);
            goList.Sort(static (a, b) =>
            {
                int c = b.PingPongCount.CompareTo(a.PingPongCount);
                if (c != 0) return c;
                c = b.MultiOverrideCount.CompareTo(a.MultiOverrideCount);
                if (c != 0) return c;
                return b.InsignificantCount.CompareTo(a.InsignificantCount);
            });
            report.GameObjects = goList;

            report.GoPathToInstanceRoot = goPathToRoot;
            report.IsComplete = true;
            EndRun();
            report.AnalysisTimeMs = sw.ElapsedMilliseconds;
            yield return 1f;
        }

        /// <summary>
        /// Process a batch of instances: collect overrides on main thread
        /// (Unity API), then classify conflicts in parallel (pure computation),
        /// then merge results on main thread.
        /// </summary>
        private void ProcessBatchParallel(
            List<(GameObject instanceRoot, string hierPath,
                List<NestingLevel> chain)> batch,
            Dictionary<string, GameObjectReport> goReports,
            Dictionary<string, GameObject> goPathToRoot,
            AnalysisReport report)
        {
            // ── Main thread: build overrideMaps (Unity API calls) ──
            var prepared = new (
                Dictionary<PropertyKey, List<OverrideEntry>> overrideMap,
                string hierPath,
                List<NestingLevel> chain,
                GameObject instanceRoot
            )[batch.Count];

            for (int i = 0; i < batch.Count; i++)
            {
                var (instanceRoot, hierPath, chain) = batch[i];
                var overrideMap = new Dictionary<PropertyKey, List<OverrideEntry>>();
                CollectOverridesFast(chain, overrideMap);
                prepared[i] = (overrideMap, hierPath, chain, instanceRoot);
            }

            // ── Parallel: classify conflicts (no Unity API) ──
            var results = new (
                List<PropertyConflict> conflicts,
                GameObject instanceRoot
            )[batch.Count];

            Parallel.For(0, batch.Count, i =>
            {
                var (overrideMap, hierPath, chain, instanceRoot) = prepared[i];
                var instanceConflicts = new List<PropertyConflict>();

                // Scalar properties.
                foreach (var kvp in overrideMap)
                {
                    if (ComparerRouter.IsQuaternionComponent(kvp.Key.PropertyPath))
                        continue;

                    var prefixedKey = new PropertyKey
                    {
                        ComponentType = kvp.Key.ComponentType,
                        GameObjectPath = hierPath.Length > 0
                            ? $"{hierPath}/{kvp.Key.GameObjectPath}"
                            : kvp.Key.GameObjectPath,
                        PropertyPath = kvp.Key.PropertyPath,
                        TargetInstanceId = kvp.Key.TargetInstanceId
                    };

                    var conflict = ClassifyConflict(prefixedKey, kvp.Value, chain);
                    if (conflict != null)
                        instanceConflicts.Add(conflict);
                }

                // Quaternion groups.
                var qGroups = GroupQuaternionOverrides(overrideMap);
                foreach (var qg in qGroups)
                {
                    var prefixedKey = new PropertyKey
                    {
                        ComponentType = qg.BaseKey.ComponentType,
                        GameObjectPath = hierPath.Length > 0
                            ? $"{hierPath}/{qg.BaseKey.GameObjectPath}"
                            : qg.BaseKey.GameObjectPath,
                        PropertyPath = qg.BaseKey.PropertyPath,
                        TargetInstanceId = qg.BaseKey.TargetInstanceId
                    };

                    var prefixedQg = new QuaternionGroup
                    {
                        BaseKey = prefixedKey,
                        ValuesByDepth = qg.ValuesByDepth,
                        AssetPathsByDepth = qg.AssetPathsByDepth
                    };

                    var conflict = ClassifyQuaternionGroup(prefixedQg);
                    if (conflict != null)
                        instanceConflicts.Add(conflict);
                }

                results[i] = (instanceConflicts, instanceRoot);
            });

            // ── Main thread: merge results into report ──
            for (int i = 0; i < results.Length; i++)
            {
                var (conflicts, instanceRoot) = results[i];
                if (conflicts == null) continue;

                foreach (var conflict in conflicts)
                {
                    AddConflictToReport(goReports, conflict, report);
                    goPathToRoot.TryAdd(conflict.Key.GameObjectPath, instanceRoot);
                }
            }
        }

        /// <summary>
        /// Release per-run state (SerializedObject cache, ignore snapshot,
        /// GO/component caches) without completing an analysis. Call this
        /// from the pump loop if the user cancels an incremental run.
        /// </summary>
        public void AbortRun()
        {
            EndRun();
        }

        /// <summary>
        /// Recursively find all GameObjects that are PrefabInstance roots.
        /// </summary>
        private void CollectPrefabInstanceRoots(Transform parent,
            System.Text.StringBuilder pathBuilder,
            List<(GameObject go, string hierarchyPath)> results)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                int prevLen = pathBuilder.Length;
                if (prevLen > 0) pathBuilder.Append('/');
                pathBuilder.Append(child.name);

                if (PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                {
                    results.Add((child.gameObject, pathBuilder.ToString()));
                }

                CollectPrefabInstanceRoots(child, pathBuilder, results);
                pathBuilder.Length = prevLen; // restore
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
            int myRunId = _runId;
            report.AnalyzedRoot = root;
            report.Chain = BuildChain(root);

            // Collect dependent asset paths for the health scan.
            foreach (var level in report.Chain)
                if (!string.IsNullOrEmpty(level.AssetPath) && !level.IsSceneInstance)
                    report.DependentAssetPaths.Add(level.AssetPath);

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
                {
                    if (_runId != myRunId) yield break;
                    yield return (float)processed / totalKeys;
                }
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
                        PropertyPath = "m_LocalRotation [Q]",
                        // Quaternion groups are synthetic aggregations of
                        // four axis mods whose individual TargetInstanceIds
                        // may differ. Transforms only ever have one rotation
                        // per GO so collisions are not a concern here.
                        TargetInstanceId = 0
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

            if (qg.ValuesByDepth.Count < 2) return null;

            // Sort depths without LINQ — typically 2-5 items.
            var depths = new List<int>(qg.ValuesByDepth.Keys);
            depths.Sort();

            // All same rotation?
            bool allSame = true;
            var first = qg.ValuesByDepth[depths[0]];
            for (int i = 1; i < depths.Count; i++)
            {
                if (Mathf.Abs(Quaternion.Dot(first, qg.ValuesByDepth[depths[i]])) < dotThreshold)
                { allSame = false; break; }
            }

            var entries = new List<OverrideEntry>(depths.Count);
            for (int i = 0; i < depths.Count; i++)
            {
                int d = depths[i];
                entries.Add(new OverrideEntry
                {
                    Depth = d,
                    Value = FmtQ(qg.ValuesByDepth[d]),
                    AssetPath = qg.AssetPathsByDepth.GetValueOrDefault(d, "")
                });
            }

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
                // Known-noise categories are always Insignificant without SO.
                if (conflict.Category == OverrideCategory.NetworkNoise
                    || conflict.Category == OverrideCategory.Lightmap)
                {
                    conflict.Severity = ConflictSeverity.Insignificant;
                    return conflict;
                }

                // FastClassify (hierarchy mode): skip the expensive
                // SerializedObject-based CheckInsignificant. All single-
                // depth overrides are classified as Insignificant. This
                // slightly overcounts (a real override with one depth
                // entry gets marked insignificant instead of null), but
                // PingPong/Multi/Orphan detection is unaffected.
                if (FastClassify)
                {
                    conflict.Severity = ConflictSeverity.Insignificant;
                    return conflict;
                }

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
            // Unity overloads == for destroyed objects; guard against stale refs.
            if (obj == null) return null;

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
                PropertyPath = mod.propertyPath,
                // Disambiguate sibling components that share a type name —
                // e.g. a GameObject hosting multiple NetworkBehaviour
                // subclasses all reported as the same GetType().Name — so
                // their parallel overrides don't merge into one bogus
                // MultiOverride conflict with duplicate same-depth entries.
                TargetInstanceId = mod.target != null ? mod.target.GetInstanceID() : 0
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
