using System;
using System.Collections.Generic;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace SashaRX.PrefabDoctor
{
    public class PrefabDoctorWindow : EditorWindow
    {
        [MenuItem("Tools/Prefab Doctor %&o")]
        private static void Open() => GetWindow<PrefabDoctorWindow>("Prefab Doctor");

        // ── State ──────────────────────────────────────────────────
        private int _activeTab; // 0 = Instance Analysis, 1 = Project Scan
        private static readonly string[] s_TabNames = { "Instance Analysis", "Project Scan" };

        private GameObject _target;
        private AnalysisReport _report;
        private OverrideAnalyzer _analyzer = new();

        // Incremental analysis (instance mode)
        private IEnumerator<float> _incrementalJob;
        private AnalysisReport _pendingReport;
        private float _progress;
        private bool _useIncremental = true;

        // Incremental hierarchy analysis — separate job so it does not
        // collide with instance-mode incremental state. Pumped from
        // PumpHierarchyJob via EditorApplication.update.
        private IEnumerator<float> _hierarchyJob;
        private AnalysisReport _pendingHierarchyReport;
        private int _hierarchyInstancesTotal;

        // Project scan (separate panel)
        private ProjectScanPanel _scanPanel = new();

        // Filter toggles
        private bool _showDefaults;
        private bool _showSceneOverrides;
        private bool _showInternalProps;
        private FilterMode _filterMode = FilterMode.ConflictsOnly;

        // UI state
        private Vector2 _leftScroll;
        private int _selectedGoIndex = -1;

        // Selection for batch ops. Stable across left-panel GameObject
        // switches and filter changes — the handle is (canonical GoReport
        // index, ConflictIndex inside that GoReport), not a position in
        // the currently visible slice. Invalidated only when _report
        // changes by reference.
        private readonly HashSet<ConflictHandle> _selectedConflicts = new();

        // Cached filtered GameObject view. OnGUI can run many times per
        // second; without this cache each repaint re-walks the report and
        // allocates a fresh List via LINQ Where().ToList().
        private List<GameObjectReport> _filteredGOCache;
        private AnalysisReport _filteredGOCacheReport;
        private FilterMode _filteredGOCacheMode;

        // Reverse map: GameObjectReport → canonical index in _report.GameObjects.
        // Rebuilt once per new AnalysisReport so ConflictHandle can carry a
        // stable index without walking _report.GameObjects on every lookup.
        private Dictionary<GameObjectReport, int> _goReportIndexMap;
        private AnalysisReport _goReportIndexMapReport;

        // Cached category counts — CountByCategory iterates every conflict
        // in every GameObjectReport, which on a hierarchy run with 300k+
        // objects turns into millions of operations per Repaint if invoked
        // from DrawStatusBar unconditionally. Invalidated on report change.
        private Dictionary<OverrideCategory, int> _categoryCountsCache;
        private AnalysisReport _categoryCountsCacheReport;

        // Hard render cap for the (still-IMGUI) left panel in Phase 1.
        // Phase 2 replaces DrawGameObjectList with a virtualised ListView
        // and this cap goes away.
        private const int k_MaxVisibleGOs = 500;

        // ── UI Toolkit elements (Phase 1 hybrid) ───────────────────
        private VisualElement _imguiShellTop;       // tab bar + toolbar + status bar host
        private VisualElement _instanceTab;
        private VisualElement _scanTab;
        private TwoPaneSplitView _instanceSplit;
        private IMGUIContainer _leftPanelImgui;
        private VisualElement _rightPanel;
        private IMGUIContainer _rightPanelHeaderImgui;
        private VisualElement _batchBar;
        private Label _batchCountLabel;
        private MultiColumnListView _conflictListView;
        private IMGUIContainer _emptyStateImgui;

        // Backing list for MultiColumnListView. Rebuilt on GO selection or
        // filter change; handed to MCLV as itemsSource.
        private readonly List<ConflictRow> _conflictRows = new();

        /// <summary>
        /// Stable reference to a single conflict across left-panel GameObject
        /// switches and filter changes. Indices are canonical (into
        /// <see cref="AnalysisReport.GameObjects"/> and
        /// <see cref="GameObjectReport.Conflicts"/>), so a selection made on
        /// GO_A survives the user clicking through GO_B, GO_C, then back.
        /// </summary>
        internal readonly struct ConflictHandle : IEquatable<ConflictHandle>
        {
            public readonly int GoReportIndex;
            public readonly int ConflictIndex;

            public ConflictHandle(int goReportIndex, int conflictIndex)
            {
                GoReportIndex = goReportIndex;
                ConflictIndex = conflictIndex;
            }

            public bool Equals(ConflictHandle other) =>
                GoReportIndex == other.GoReportIndex && ConflictIndex == other.ConflictIndex;

            public override bool Equals(object obj) => obj is ConflictHandle o && Equals(o);

            public override int GetHashCode() =>
                unchecked((GoReportIndex * 397) ^ ConflictIndex);
        }

        /// <summary>
        /// One row in the right-panel conflict list view. Holds the canonical
        /// handle so selection persists across rebuilds, plus a direct ref to
        /// the conflict for O(1) column binding.
        /// </summary>
        private readonly struct ConflictRow
        {
            public readonly ConflictHandle Handle;
            public readonly PropertyConflict Conflict;
            public readonly GameObjectReport GoReport;

            public ConflictRow(ConflictHandle handle, PropertyConflict conflict,
                GameObjectReport goReport)
            {
                Handle = handle;
                Conflict = conflict;
                GoReport = goReport;
            }
        }

        private enum FilterMode
        {
            ConflictsOnly,
            AllOverrides,
            PingPongOnly,
            OrphansOnly,
            InsignificantOnly,
            LightmapOnly,
            NetworkNoiseOnly,
            /// <summary>Rendering-related overrides: Lightmap + StaticFlags + Material + Transform.</summary>
            GraphicsOnly,
            /// <summary>Transform category only (position / rotation / scale deltas).</summary>
            TransformOnly,
            /// <summary>Orphans + any MultiOverride — the "broken or redundant" set.</summary>
            GarbageOnly
        }

        // ── Main Layout ────────────────────────────────────────────
        //
        // Phase 1 hybrid: the window's root is a UI Toolkit VisualElement
        // tree, but everything except the right-panel conflict list is
        // still drawn via IMGUIContainer — we have not rewritten the
        // toolbar, status bar, left panel, or project scan panel yet. The
        // big win is that the right panel's conflict table is now a real
        // MultiColumnListView with full row virtualisation, which both
        // removes the k_MaxVisibleConflicts row cap and lets the batch
        // selection carry a stable ConflictHandle across GameObject
        // switches and filter changes. Phase 2 will port the rest.

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;

            // Tab bar + toolbar + status bar live in a single IMGUIContainer
            // that renders the pre-existing DrawToolbar / DrawStatusBar
            // paths. The tab bar itself is drawn here too so tab switching
            // stays entirely IMGUI-controlled until Phase 2.
            _imguiShellTop = new IMGUIContainer(DrawShellTopImgui);
            _imguiShellTop.style.flexShrink = 0;
            root.Add(_imguiShellTop);

            // Instance Analysis tab body.
            _instanceTab = new VisualElement();
            _instanceTab.style.flexGrow = 1;
            _instanceTab.style.flexDirection = FlexDirection.Column;
            root.Add(_instanceTab);

            // Empty state host: shown when the report is null or has no
            // GameObjectReports. Same DrawEmptyState body as before.
            _emptyStateImgui = new IMGUIContainer(DrawEmptyState);
            _emptyStateImgui.style.flexGrow = 1;
            _instanceTab.Add(_emptyStateImgui);

            // Split view is mounted on demand so TwoPaneSplitView always
            // has exactly its two required children and the empty state
            // path does not need to fight it for layout.
            _instanceSplit = new TwoPaneSplitView(
                fixedPaneIndex: 0,
                fixedPaneStartDimension: 260f,
                orientation: TwoPaneSplitViewOrientation.Horizontal)
            {
                viewDataKey = "prefab-doctor-instance-split"
            };
            _instanceSplit.style.flexGrow = 1;
            _instanceSplit.style.display = DisplayStyle.None;
            _instanceTab.Add(_instanceSplit);

            _leftPanelImgui = new IMGUIContainer(DrawGameObjectListScrolled);
            _leftPanelImgui.style.flexGrow = 1;
            _instanceSplit.Add(_leftPanelImgui);

            _rightPanel = BuildRightPanel();
            _instanceSplit.Add(_rightPanel);

            // Project Scan tab body — still 100% IMGUI.
            _scanTab = new VisualElement();
            _scanTab.style.flexGrow = 1;
            _scanTab.style.display = DisplayStyle.None;
            var scanImgui = new IMGUIContainer(() => _scanPanel.OnGUI());
            scanImgui.style.flexGrow = 1;
            _scanTab.Add(scanImgui);
            root.Add(_scanTab);

            RefreshTabVisibility();
            RebuildConflictList();
            UpdateBatchBar();
        }

        /// <summary>
        /// IMGUI shell that draws the tab bar + (on the instance tab) the
        /// toolbar and status bar. Auto-height because it adapts to which
        /// sub-sections draw based on current state.
        /// </summary>
        private void DrawShellTopImgui()
        {
            // Tab bar
            int newTab = GUILayout.Toolbar(_activeTab, s_TabNames, GUILayout.Height(22));
            if (newTab != _activeTab)
            {
                _activeTab = newTab;
                RefreshTabVisibility();
            }

            if (_activeTab != 0) return;

            DrawToolbar();
            DrawStatusBar();

            // Layout visibility for the body: either the split view (with
            // the conflict list) or the empty state, but never both.
            bool haveReport = _report != null && _report.GameObjects.Count > 0;
            SetDisplay(_instanceSplit, haveReport);
            SetDisplay(_emptyStateImgui, !haveReport);
        }

        private void RefreshTabVisibility()
        {
            if (_instanceTab == null) return;
            SetDisplay(_instanceTab, _activeTab == 0);
            SetDisplay(_scanTab, _activeTab == 1);
        }

        private static void SetDisplay(VisualElement element, bool visible)
        {
            if (element == null) return;
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// IMGUI left-panel wrapper that preserves the legacy scroll view.
        /// Phase 2 replaces this with a ListView.
        /// </summary>
        private void DrawGameObjectListScrolled()
        {
            _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll);
            DrawGameObjectList();
            EditorGUILayout.EndScrollView();
        }

        // ── Toolbar ────────────────────────────────────────────────

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Target field
            var newTarget = (GameObject)EditorGUILayout.ObjectField(
                _target, typeof(GameObject), true, GUILayout.Width(200));
            if (newTarget != _target)
            {
                _target = newTarget;
                _report = null;
            }

            // Use selection
            if (GUILayout.Button("← Selection", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                if (Selection.activeGameObject != null)
                {
                    _target = Selection.activeGameObject;
                    _report = null;
                }
            }

            GUILayout.Space(8);

            // Analyze button
            GUI.backgroundColor = _target != null ? new Color(0.4f, 0.8f, 0.4f) : Color.gray;
            if (GUILayout.Button("Analyze", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                RunAnalysis();
            }
            // Hierarchy mode — the important one
            GUI.backgroundColor = _target != null ? new Color(0.3f, 0.6f, 1f) : Color.gray;
            if (GUILayout.Button("▼ Hierarchy", EditorStyles.toolbarButton, GUILayout.Width(85)))
            {
                RunHierarchyAnalysis();
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(8);

            // Filter mode — rebuild conflict list on change so the
            // MultiColumnListView picks up the new set. Selection
            // (by stable ConflictHandle) survives the rebuild.
            var newFilter = (FilterMode)EditorGUILayout.EnumPopup(_filterMode,
                EditorStyles.toolbarDropDown, GUILayout.Width(130));
            if (newFilter != _filterMode)
            {
                _filterMode = newFilter;
                RebuildConflictList();
            }

            GUILayout.FlexibleSpace();

            // Toggles
            _showDefaults = GUILayout.Toggle(_showDefaults, "Defaults",
                EditorStyles.toolbarButton, GUILayout.Width(60));
            _showSceneOverrides = GUILayout.Toggle(_showSceneOverrides, "Scene",
                EditorStyles.toolbarButton, GUILayout.Width(50));
            _showInternalProps = GUILayout.Toggle(_showInternalProps, "Internal",
                EditorStyles.toolbarButton, GUILayout.Width(55));

            GUILayout.Space(4);

            // Batch actions
            if (_report != null)
            {
                // In hierarchy mode, Clean Orphans operates on EVERY prefab
                // instance root in the current report (bulk), not just
                // _target. A single scene can easily have ~1000 orphan mods
                // per instance, so the scoped variant is basically useless
                // for large levels. Label and confirmation dialog change
                // so the user knows what they're about to do.
                bool hierarchy = _report.IsHierarchyMode
                    && _report.HierarchyInstanceRoots != null
                    && _report.HierarchyInstanceRoots.Count > 0;

                string cleanLabel = hierarchy
                    ? $"Clean Orphans ({_report.HierarchyInstanceRoots.Count})"
                    : "Clean Orphans";
                float cleanWidth = hierarchy ? 140f : 90f;

                if (GUILayout.Button(cleanLabel, EditorStyles.toolbarButton,
                        GUILayout.Width(cleanWidth)))
                {
                    if (hierarchy)
                        DoCleanOrphansHierarchy();
                    else
                    {
                        int removed = OverrideActions.CleanOrphans(_target);
                        Debug.Log($"[Prefab Doctor] Cleaned {removed} orphaned overrides");
                        RunAnalysis();
                    }
                }

                if (GUILayout.Button("Clean Insignificant", EditorStyles.toolbarButton, GUILayout.Width(110)))
                {
                    int removed = OverrideActions.CleanInsignificant(_target, _report.Chain);
                    Debug.Log($"[Prefab Doctor] Cleaned {removed} insignificant overrides");
                    RunAnalysis();
                }

                if (GUILayout.Button("Copy Report", EditorStyles.toolbarButton, GUILayout.Width(85)))
                {
                    string md = OverrideReportFormatter.ToMarkdown(_report);
                    EditorGUIUtility.systemCopyBuffer = md;
                    Debug.Log($"[Prefab Doctor] Copied report ({md.Length} chars) to clipboard");
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ── Status Bar ─────────────────────────────────────────────

        private void DrawStatusBar()
        {
            if (_report == null) return;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            if (_report.IsHierarchyMode)
            {
                GUILayout.Label($"HIERARCHY: {_report.InstancesAnalyzed} instances, " +
                                $"{_report.GameObjects.Count} objects with overrides",
                    EditorStyles.miniLabel);
            }
            else
            {
                var chainNames = string.Join(" → ",
                    _report.Chain.Select(l => l.IsSceneInstance ? "[Scene]" :
                        System.IO.Path.GetFileNameWithoutExtension(l.AssetPath)));
                GUILayout.Label($"Chain: {chainNames}", EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            DrawBadge($"PP:{_report.TotalPingPong}", Color.red, _report.TotalPingPong > 0);
            DrawBadge($"Multi:{_report.TotalMultiOverride}", new Color(1f, 0.7f, 0f),
                _report.TotalMultiOverride > 0);
            DrawBadge($"Orphan:{_report.TotalOrphan}", new Color(0.6f, 0.6f, 0.6f),
                _report.TotalOrphan > 0);
            DrawBadge($"Insig:{_report.TotalInsignificant}", new Color(0.5f, 0.8f, 1f),
                _report.TotalInsignificant > 0);

            var catCounts = GetCategoryCounts();
            GUILayout.Label("│", EditorStyles.miniLabel);
            DrawBadge($"Lightmap:{catCounts[OverrideCategory.Lightmap]}",
                new Color(1f, 0.9f, 0.3f), catCounts[OverrideCategory.Lightmap] > 0);
            DrawBadge($"Net:{catCounts[OverrideCategory.NetworkNoise]}",
                new Color(0.4f, 0.9f, 0.7f), catCounts[OverrideCategory.NetworkNoise] > 0);
            DrawBadge($"Flags:{catCounts[OverrideCategory.StaticFlags]}",
                new Color(0.8f, 0.7f, 1f), catCounts[OverrideCategory.StaticFlags] > 0);
            int otherCount = catCounts[OverrideCategory.General]
                + catCounts[OverrideCategory.Transform]
                + catCounts[OverrideCategory.Name]
                + catCounts[OverrideCategory.Material];
            DrawBadge($"Other:{otherCount}", new Color(0.7f, 0.7f, 0.7f), otherCount > 0);

            GUILayout.Label($"  {_report.AnalysisTimeMs:F0}ms", EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawBadge(string text, Color color, bool active)
        {
            var prevColor = GUI.contentColor;
            GUI.contentColor = active ? color : new Color(0.5f, 0.5f, 0.5f);
            GUILayout.Label(text, EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(false));
            GUI.contentColor = prevColor;
        }

        private Dictionary<OverrideCategory, int> GetCategoryCounts()
        {
            if (ReferenceEquals(_categoryCountsCacheReport, _report)
                && _categoryCountsCache != null)
            {
                return _categoryCountsCache;
            }

            var counts = new Dictionary<OverrideCategory, int>
            {
                [OverrideCategory.General] = 0,
                [OverrideCategory.Transform] = 0,
                [OverrideCategory.Lightmap] = 0,
                [OverrideCategory.NetworkNoise] = 0,
                [OverrideCategory.StaticFlags] = 0,
                [OverrideCategory.Name] = 0,
                [OverrideCategory.Material] = 0,
            };
            if (_report != null)
            {
                foreach (var go in _report.GameObjects)
                foreach (var c in go.Conflicts)
                    counts[c.Category]++;
            }

            _categoryCountsCache = counts;
            _categoryCountsCacheReport = _report;
            return counts;
        }

        private static Dictionary<OverrideCategory, int> CountByCategory(AnalysisReport report)
        {
            var counts = new Dictionary<OverrideCategory, int>
            {
                [OverrideCategory.General] = 0,
                [OverrideCategory.Transform] = 0,
                [OverrideCategory.Lightmap] = 0,
                [OverrideCategory.NetworkNoise] = 0,
                [OverrideCategory.StaticFlags] = 0,
                [OverrideCategory.Name] = 0,
                [OverrideCategory.Material] = 0,
            };
            foreach (var go in report.GameObjects)
            foreach (var c in go.Conflicts)
                counts[c.Category]++;
            return counts;
        }

        // ── Left Panel: GameObject Tree ────────────────────────────

        private void DrawGameObjectList()
        {
            var filteredGOs = GetFilteredGameObjects();
            int visibleCount = Mathf.Min(filteredGOs.Count, k_MaxVisibleGOs);

            for (int i = 0; i < visibleCount; i++)
            {
                var goReport = filteredGOs[i];
                bool selected = _selectedGoIndex == i;

                EditorGUILayout.BeginHorizontal(selected
                    ? "SelectionRect"
                    : GUIStyle.none);

                // Severity icon
                if (goReport.PingPongCount > 0)
                    DrawColorDot(Color.red);
                else if (goReport.MultiOverrideCount > 0)
                    DrawColorDot(new Color(1f, 0.7f, 0f));
                else if (goReport.OrphanCount > 0)
                    DrawColorDot(new Color(0.6f, 0.6f, 0.6f));
                else
                    DrawColorDot(new Color(0.5f, 0.8f, 1f));

                string displayName = goReport.RelativePath.Contains('/')
                    ? goReport.RelativePath[(goReport.RelativePath.LastIndexOf('/') + 1)..]
                    : goReport.RelativePath;

                if (GUILayout.Button(displayName, EditorStyles.label))
                {
                    _selectedGoIndex = i;

                    // Phase 1: selection is keyed by stable ConflictHandle,
                    // so switching left-panel GameObject no longer clears
                    // the set. Previously-picked conflicts on other GOs
                    // stay in the batch until the user reverts or explicitly
                    // presses Select None. Rebuild the MCLV source for the
                    // new GameObject and push stored selection back in.
                    RebuildConflictList();

                    // Ping + select the actual scene GameObject so the user
                    // can jump straight from a row in the conflict list to
                    // the object in the Hierarchy / Scene view. For reports
                    // built from prefab-asset levels deep in the chain the
                    // resolver returns null (no scene representation) and we
                    // quietly skip the ping.
                    var sceneGO = ResolveByRelativePath(goReport.RelativePath);
                    if (sceneGO != null)
                    {
                        EditorGUIUtility.PingObject(sceneGO);
                        Selection.activeGameObject = sceneGO;
                    }
                }

                GUILayout.FlexibleSpace();

                string counts = "";
                if (goReport.PingPongCount > 0) counts += $"P:{goReport.PingPongCount} ";
                if (goReport.MultiOverrideCount > 0) counts += $"M:{goReport.MultiOverrideCount} ";
                if (goReport.OrphanCount > 0) counts += $"O:{goReport.OrphanCount} ";
                GUILayout.Label(counts, EditorStyles.miniLabel);

                EditorGUILayout.EndHorizontal();
            }

            if (filteredGOs.Count > visibleCount)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    $"Showing first {visibleCount} of {filteredGOs.Count} GameObjects.\n"
                    + "Narrow the filter (PingPongOnly / MultiOverrideOnly / a category) "
                    + "to see fewer rows, or use Copy Report for the full list.",
                    MessageType.Info);
            }
        }

        private void DrawColorDot(Color color)
        {
            var dotRect = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10), GUILayout.Height(16));
            dotRect.y += 3;
            dotRect.height = 10;
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(dotRect, EditorGUIUtility.whiteTexture, ScaleMode.ScaleToFit);
            GUI.color = prev;
        }

        /// <summary>
        /// Resolve a <see cref="GameObjectReport.RelativePath"/> back to a
        /// live scene GameObject. The path was produced by
        /// <c>OverrideAnalyzer.GetRelativePath</c> which walks parents all
        /// the way up to the scene root, so it always starts with the top
        /// level GameObject's name.
        ///
        /// Resolution order:
        ///   1. Walk from <c>_target</c> if the path starts with its name.
        ///      Fast path for the common case where the analyzed root is
        ///      the same object we need to ping.
        ///   2. Walk scene roots in the active scene.
        ///
        /// Returns null if the path corresponds to an object that lives
        /// inside a prefab asset (not in the scene) — nothing to ping.
        /// </summary>
        private GameObject ResolveByRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "?") return null;

            // Fast path: resolve via _target if its name matches the root.
            if (_target != null)
            {
                string rootName = _target.name;
                if (path == rootName) return _target;
                if (path.StartsWith(rootName + "/", StringComparison.Ordinal))
                {
                    string rest = path[(rootName.Length + 1)..];
                    var child = _target.transform.Find(rest);
                    if (child != null) return child.gameObject;
                }
            }

            // Fallback: walk scene roots. The active scene is where the
            // analyzed instance lives on hierarchy mode, so this catches
            // paths whose first segment is not _target (e.g. when the user
            // analyzed a sub-tree but the report contains parent paths).
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid()) return null;

            int slash = path.IndexOf('/');
            string firstName = slash < 0 ? path : path[..slash];

            var rootGOs = activeScene.GetRootGameObjects();
            for (int i = 0; i < rootGOs.Length; i++)
            {
                if (rootGOs[i].name != firstName) continue;
                if (slash < 0) return rootGOs[i];

                string rest = path[(slash + 1)..];
                var child = rootGOs[i].transform.Find(rest);
                if (child != null) return child.gameObject;
            }

            return null;
        }

        // ── Right Panel: Conflict Table ────────────────────────────

        // ── Right Panel: UI Toolkit Conflict Table ─────────────────

        /// <summary>
        /// Build the right-panel subtree: GameObject header (still IMGUI
        /// for Phase 1), batch action toolbar, and the virtualised
        /// MultiColumnListView that replaces the old IMGUI DrawConflictList.
        /// </summary>
        private VisualElement BuildRightPanel()
        {
            var panel = new VisualElement();
            panel.style.flexGrow = 1;
            panel.style.flexDirection = FlexDirection.Column;

            _rightPanelHeaderImgui = new IMGUIContainer(DrawConflictListHeaderImgui);
            _rightPanelHeaderImgui.style.flexShrink = 0;
            panel.Add(_rightPanelHeaderImgui);

            _batchBar = BuildBatchBar();
            panel.Add(_batchBar);

            var columns = new Columns();

            var sevCol = new Column { name = "sev", title = "Sev", stretchable = false };
            sevCol.width = 40f;
            sevCol.makeCell = MakeLabelCell;
            sevCol.bindCell = BindSevCell;
            columns.Add(sevCol);

            var catCol = new Column { name = "cat", title = "Cat", stretchable = false };
            catCol.width = 80f;
            catCol.makeCell = MakeLabelCell;
            catCol.bindCell = BindCatCell;
            columns.Add(catCol);

            var compCol = new Column { name = "comp", title = "Component", stretchable = false };
            compCol.width = 140f;
            compCol.makeCell = MakeLabelCell;
            compCol.bindCell = BindCompCell;
            columns.Add(compCol);

            var propCol = new Column { name = "prop", title = "Property", stretchable = false };
            propCol.width = 240f;
            propCol.makeCell = MakeLabelCell;
            propCol.bindCell = BindPropCell;
            columns.Add(propCol);

            var valsCol = new Column { name = "vals", title = "Values by depth", stretchable = true };
            valsCol.minWidth = 200f;
            valsCol.makeCell = MakeLabelCell;
            valsCol.bindCell = BindValsCell;
            columns.Add(valsCol);

            _conflictListView = new MultiColumnListView(columns)
            {
                itemsSource = _conflictRows,
                selectionType = SelectionType.Multiple,
                reorderable = false,
                fixedItemHeight = 20f,
                showBorder = true,
                showBoundCollectionSize = false,
                showFoldoutHeader = false,
                showAlternatingRowBackgrounds = AlternatingRowBackground.None,
                horizontalScrollingEnabled = false,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                viewDataKey = "prefab-doctor-conflict-list"
            };
            _conflictListView.style.flexGrow = 1;
            _conflictListView.selectionChanged += OnConflictListSelectionChanged;
            panel.Add(_conflictListView);

            return panel;
        }

        private Label MakeLabelCell()
        {
            var lbl = new Label
            {
                style =
                {
                    unityTextAlign = TextAnchor.MiddleLeft,
                    marginLeft = 4,
                    marginRight = 4,
                    overflow = Overflow.Hidden,
                    textOverflow = TextOverflow.Ellipsis,
                    whiteSpace = WhiteSpace.NoWrap
                }
            };
            lbl.AddManipulator(new ContextualMenuManipulator(PopulateRowContextMenu));
            return lbl;
        }

        private void BindSevCell(VisualElement element, int index)
        {
            var lbl = (Label)element;
            if (index < 0 || index >= _conflictRows.Count) { lbl.text = ""; return; }
            var row = _conflictRows[index];
            lbl.userData = index;

            lbl.text = row.Conflict.Severity switch
            {
                ConflictSeverity.PingPong => "PP",
                ConflictSeverity.MultiOverride => "M",
                ConflictSeverity.Orphan => "O",
                ConflictSeverity.Insignificant => "~",
                _ => "?"
            };
            lbl.style.color = row.Conflict.Severity switch
            {
                ConflictSeverity.PingPong => new Color(1f, 0.45f, 0.45f),
                ConflictSeverity.MultiOverride => new Color(1f, 0.78f, 0.25f),
                ConflictSeverity.Orphan => new Color(0.75f, 0.75f, 0.75f),
                ConflictSeverity.Insignificant => new Color(0.55f, 0.82f, 1f),
                _ => new Color(0.85f, 0.85f, 0.85f)
            };
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
        }

        private void BindCatCell(VisualElement element, int index)
        {
            var lbl = (Label)element;
            if (index < 0 || index >= _conflictRows.Count) { lbl.text = ""; return; }
            var row = _conflictRows[index];
            lbl.userData = index;
            lbl.text = row.Conflict.Category.ToString();
            lbl.style.color = row.Conflict.Category switch
            {
                OverrideCategory.Lightmap => new Color(1f, 0.9f, 0.35f),
                OverrideCategory.NetworkNoise => new Color(0.45f, 0.9f, 0.7f),
                OverrideCategory.StaticFlags => new Color(0.82f, 0.7f, 1f),
                OverrideCategory.Transform => new Color(0.75f, 0.85f, 1f),
                OverrideCategory.Material => new Color(1f, 0.7f, 0.9f),
                OverrideCategory.Name => new Color(0.95f, 0.85f, 0.6f),
                _ => new Color(0.78f, 0.78f, 0.78f)
            };
            lbl.style.unityFontStyleAndWeight = FontStyle.Normal;
        }

        private void BindCompCell(VisualElement element, int index)
        {
            var lbl = (Label)element;
            if (index < 0 || index >= _conflictRows.Count) { lbl.text = ""; return; }
            var row = _conflictRows[index];
            lbl.userData = index;
            lbl.text = row.Conflict.Key.ComponentType;
            lbl.style.color = new Color(0.85f, 0.85f, 0.85f);
            lbl.style.unityFontStyleAndWeight = FontStyle.Normal;
        }

        private void BindPropCell(VisualElement element, int index)
        {
            var lbl = (Label)element;
            if (index < 0 || index >= _conflictRows.Count) { lbl.text = ""; return; }
            var row = _conflictRows[index];
            lbl.userData = index;
            lbl.text = row.Conflict.Key.PropertyPath;
            lbl.style.color = new Color(0.85f, 0.85f, 0.85f);
            lbl.style.unityFontStyleAndWeight = FontStyle.Normal;
        }

        private void BindValsCell(VisualElement element, int index)
        {
            var lbl = (Label)element;
            if (index < 0 || index >= _conflictRows.Count) { lbl.text = ""; return; }
            var row = _conflictRows[index];
            lbl.userData = index;
            lbl.text = string.Join(" → ", row.Conflict.Overrides.Select(o =>
                $"D{o.Depth}:{TruncateValue(o.Value, 12)}"));
            lbl.style.color = new Color(0.8f, 0.8f, 0.8f);
            lbl.style.unityFontStyleAndWeight = FontStyle.Normal;
        }

        private void DrawConflictListHeaderImgui()
        {
            if (_report == null) return;

            var filteredGOs = GetFilteredGameObjects();
            if (_selectedGoIndex < 0 || _selectedGoIndex >= filteredGOs.Count)
            {
                EditorGUILayout.HelpBox(
                    "Select a GameObject on the left to view its overrides.",
                    MessageType.Info);
                return;
            }

            var goReport = filteredGOs[_selectedGoIndex];
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(goReport.RelativePath, EditorStyles.boldLabel);
            if (goReport.Instance != null &&
                GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                EditorGUIUtility.PingObject(goReport.Instance);
                Selection.activeGameObject = goReport.Instance;
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── Batch action bar ───────────────────────────────────────

        private VisualElement BuildBatchBar()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.flexShrink = 0;
            bar.style.paddingLeft = 4;
            bar.style.paddingRight = 4;
            bar.style.paddingTop = 2;
            bar.style.paddingBottom = 2;
            bar.style.borderBottomWidth = 1;
            bar.style.borderBottomColor = new Color(0f, 0f, 0f, 0.3f);
            bar.style.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 0.35f);

            _batchCountLabel = new Label("0 selected");
            _batchCountLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _batchCountLabel.style.marginRight = 8;
            _batchCountLabel.style.minWidth = 140;
            bar.Add(_batchCountLabel);

            var selectAll = new Button(SelectAllVisible) { text = "Select All Visible" };
            selectAll.style.marginLeft = 0;
            bar.Add(selectAll);

            var selectNone = new Button(SelectNone) { text = "Select None" };
            bar.Add(selectNone);

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            bar.Add(spacer);

            var revert = new Button(RevertSelected) { text = "Revert Selected" };
            revert.style.color = new Color(1f, 0.85f, 0.55f);
            bar.Add(revert);

            var copy = new Button(CopySelectedPropertyPaths) { text = "Copy Paths" };
            bar.Add(copy);

            return bar;
        }

        private void UpdateBatchBar()
        {
            if (_batchCountLabel == null) return;

            int total = _selectedConflicts.Count;
            if (total == 0)
            {
                _batchCountLabel.text = "0 selected";
                _batchCountLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
                return;
            }

            // Count distinct GameObjects represented in the selection.
            var goSet = new HashSet<int>();
            foreach (var h in _selectedConflicts) goSet.Add(h.GoReportIndex);
            int goCount = goSet.Count;

            _batchCountLabel.text = goCount > 1
                ? $"{total} selected · {goCount} GameObjects"
                : $"{total} selected";
            _batchCountLabel.style.color = new Color(0.95f, 0.85f, 0.45f);
        }

        // ── Conflict list rebuild + selection sync ─────────────────

        private bool _muteSelectionSync;

        /// <summary>
        /// Rebuild the MCLV's itemsSource from the currently selected
        /// left-panel GameObject and the active filter. Called on GO
        /// change, filter change, and after every analysis run. Selection
        /// for the current GO is pushed back into MCLV's internal
        /// selection state; selections for other GameObjects stay in
        /// _selectedConflicts untouched (cross-GO batch).
        /// </summary>
        private void RebuildConflictList()
        {
            if (_conflictListView == null) return;
            _conflictRows.Clear();

            if (_report != null && _selectedGoIndex >= 0)
            {
                var filteredGOs = GetFilteredGameObjects();
                if (_selectedGoIndex < filteredGOs.Count)
                {
                    var goReport = filteredGOs[_selectedGoIndex];
                    int canonicalGoIndex = GetCanonicalGoIndex(goReport);
                    if (canonicalGoIndex >= 0)
                    {
                        var conflicts = goReport.Conflicts;
                        for (int i = 0; i < conflicts.Count; i++)
                        {
                            var c = conflicts[i];
                            if (!PassesFilter(c)) continue;
                            _conflictRows.Add(new ConflictRow(
                                new ConflictHandle(canonicalGoIndex, i), c, goReport));
                        }
                    }
                }
            }

            _conflictListView.itemsSource = _conflictRows;
            _conflictListView.RefreshItems();
            PushSelectionToListView();
            UpdateBatchBar();
        }

        /// <summary>
        /// Mirror MCLV's current selection into _selectedConflicts for
        /// handles belonging to the currently displayed GameObject. Handles
        /// for other GameObjects are left alone.
        /// </summary>
        private void OnConflictListSelectionChanged(IEnumerable<object> _)
        {
            if (_muteSelectionSync || _report == null) return;

            int canonicalGoIndex = GetCurrentCanonicalGoIndex();
            if (canonicalGoIndex < 0) return;

            _selectedConflicts.RemoveWhere(h => h.GoReportIndex == canonicalGoIndex);

            foreach (int rowIdx in _conflictListView.selectedIndices)
            {
                if (rowIdx >= 0 && rowIdx < _conflictRows.Count)
                    _selectedConflicts.Add(_conflictRows[rowIdx].Handle);
            }

            UpdateBatchBar();
        }

        /// <summary>
        /// Push stored selection for the currently displayed GameObject
        /// into MCLV's visible selection. Wrapped in _muteSelectionSync
        /// so the resulting selectionChanged callback does not echo back.
        /// </summary>
        private void PushSelectionToListView()
        {
            if (_conflictListView == null) return;

            int canonicalGoIndex = GetCurrentCanonicalGoIndex();
            var indices = new List<int>();
            if (canonicalGoIndex >= 0)
            {
                for (int i = 0; i < _conflictRows.Count; i++)
                {
                    if (_selectedConflicts.Contains(_conflictRows[i].Handle))
                        indices.Add(i);
                }
            }

            _muteSelectionSync = true;
            try
            {
                _conflictListView.SetSelectionWithoutNotify(indices);
            }
            finally
            {
                _muteSelectionSync = false;
            }
        }

        private int GetCurrentCanonicalGoIndex()
        {
            if (_report == null || _selectedGoIndex < 0) return -1;
            var filteredGOs = GetFilteredGameObjects();
            if (_selectedGoIndex >= filteredGOs.Count) return -1;
            return GetCanonicalGoIndex(filteredGOs[_selectedGoIndex]);
        }

        private int GetCanonicalGoIndex(GameObjectReport goReport)
        {
            if (_report == null) return -1;
            if (_goReportIndexMap == null
                || !ReferenceEquals(_goReportIndexMapReport, _report))
            {
                _goReportIndexMap = new Dictionary<GameObjectReport, int>(
                    _report.GameObjects.Count);
                for (int i = 0; i < _report.GameObjects.Count; i++)
                    _goReportIndexMap[_report.GameObjects[i]] = i;
                _goReportIndexMapReport = _report;
            }
            return _goReportIndexMap.TryGetValue(goReport, out var idx) ? idx : -1;
        }

        // ── Batch action handlers ──────────────────────────────────

        private void SelectAllVisible()
        {
            foreach (var row in _conflictRows)
                _selectedConflicts.Add(row.Handle);
            PushSelectionToListView();
            UpdateBatchBar();
        }

        private void SelectNone()
        {
            _selectedConflicts.Clear();
            PushSelectionToListView();
            UpdateBatchBar();
        }

        private void RevertSelected()
        {
            if (_selectedConflicts.Count == 0 || _report == null) return;

            var tasks = ResolveBatchTasks(_selectedConflicts).ToList();
            if (tasks.Count == 0)
            {
                Debug.LogWarning("[Prefab Doctor] Revert Selected: "
                    + "nothing resolved to a valid PrefabInstance root.");
                return;
            }

            OverrideActions.BatchRevert(tasks);
            Debug.Log($"[Prefab Doctor] Batch reverted {tasks.Count} conflicts");

            if (_report.IsHierarchyMode)
                RunHierarchyAnalysis();
            else
                RunAnalysis();
        }

        private void CopySelectedPropertyPaths()
        {
            if (_selectedConflicts.Count == 0 || _report == null) return;

            var sb = new System.Text.StringBuilder(_selectedConflicts.Count * 48);
            foreach (var handle in _selectedConflicts)
            {
                if (handle.GoReportIndex < 0
                    || handle.GoReportIndex >= _report.GameObjects.Count) continue;
                var go = _report.GameObjects[handle.GoReportIndex];
                if (handle.ConflictIndex < 0
                    || handle.ConflictIndex >= go.Conflicts.Count) continue;
                var c = go.Conflicts[handle.ConflictIndex];
                sb.Append(go.RelativePath).Append('/')
                  .Append(c.Key.ComponentType).Append("::")
                  .Append(c.Key.PropertyPath).Append('\n');
            }

            EditorGUIUtility.systemCopyBuffer = sb.ToString();
            Debug.Log($"[Prefab Doctor] Copied {_selectedConflicts.Count} property paths "
                + "to clipboard");
        }

        /// <summary>
        /// Resolve every selected conflict into the
        /// <c>(instanceRoot, conflict)</c> pair expected by the new
        /// <see cref="OverrideActions.BatchRevert(IEnumerable{ValueTuple{GameObject, PropertyConflict}})"/>
        /// overload. In hierarchy mode each conflict is pinned to its own
        /// nested PrefabInstance root (resolved via
        /// <see cref="ResolveByRelativePath"/> + <c>GetNearestPrefabInstanceRoot</c>).
        /// In instance mode every conflict shares <c>_target</c>.
        /// </summary>
        private IEnumerable<(GameObject instanceRoot, PropertyConflict conflict)>
            ResolveBatchTasks(IEnumerable<ConflictHandle> handles)
        {
            if (_report == null) yield break;

            foreach (var handle in handles)
            {
                if (handle.GoReportIndex < 0
                    || handle.GoReportIndex >= _report.GameObjects.Count) continue;
                var go = _report.GameObjects[handle.GoReportIndex];

                if (handle.ConflictIndex < 0
                    || handle.ConflictIndex >= go.Conflicts.Count) continue;
                var conflict = go.Conflicts[handle.ConflictIndex];

                GameObject instanceRoot;
                if (_report.IsHierarchyMode)
                {
                    var sceneGo = ResolveByRelativePath(go.RelativePath);
                    if (sceneGo == null) continue;
                    instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(sceneGo);
                    if (instanceRoot == null) continue;
                }
                else
                {
                    instanceRoot = _target;
                    if (instanceRoot == null) continue;
                }

                yield return (instanceRoot, conflict);
            }
        }

        // ── Row context menu ───────────────────────────────────────

        private void PopulateRowContextMenu(ContextualMenuPopulateEvent evt)
        {
            if (!(evt.target is VisualElement ve) || !(ve.userData is int rowIdx)) return;
            if (rowIdx < 0 || rowIdx >= _conflictRows.Count) return;

            var row = _conflictRows[rowIdx];
            var conflict = row.Conflict;

            evt.menu.AppendAction("Revert All (return to base)", _ =>
            {
                // Route through the new tasks overload so hierarchy mode
                // resolves to the right nested instance owner.
                var tasks = ResolveBatchTasks(new[] { row.Handle }).ToList();
                if (tasks.Count == 0) return;
                OverrideActions.BatchRevert(tasks);
                if (_report != null && _report.IsHierarchyMode) RunHierarchyAnalysis();
                else RunAnalysis();
            });

            evt.menu.AppendSeparator();

            foreach (var entry in conflict.Overrides)
            {
                var level = _report?.Chain.FirstOrDefault(l => l.Depth == entry.Depth);
                string name = level.HasValue && level.Value.IsSceneInstance
                    ? "Scene"
                    : (level.HasValue
                        ? System.IO.Path.GetFileNameWithoutExtension(level.Value.AssetPath)
                        : "?");
                int capturedDepth = entry.Depth;
                string capturedValue = TruncateValue(entry.Value, 20);
                evt.menu.AppendAction(
                    $"Keep only at D{capturedDepth} ({name}) = {capturedValue}",
                    _ =>
                    {
                        OverrideActions.KeepOnlyAtDepth(_target, conflict, capturedDepth);
                        if (_report != null && _report.IsHierarchyMode) RunHierarchyAnalysis();
                        else RunAnalysis();
                    });
            }

            evt.menu.AppendSeparator();

            bool isSelected = _selectedConflicts.Contains(row.Handle);
            evt.menu.AppendAction(isSelected ? "Deselect" : "Select", _ =>
            {
                if (isSelected) _selectedConflicts.Remove(row.Handle);
                else _selectedConflicts.Add(row.Handle);
                PushSelectionToListView();
                UpdateBatchBar();
            });

            evt.menu.AppendAction("Copy property path", _ =>
            {
                EditorGUIUtility.systemCopyBuffer = conflict.Key.PropertyPath;
            });
        }

        // ── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Public entry point for context menu / external code.
        /// </summary>
        public void SetTargetAndAnalyze(GameObject target, Transform subtreeRoot = null)
        {
            _target = target;
            _subtreeRoot = subtreeRoot;
            _report = null;
            RunAnalysis();
        }

        public void SetTargetAndAnalyzeHierarchy(GameObject target)
        {
            _target = target;
            _subtreeRoot = null;
            _report = null;
            RunHierarchyAnalysis();
        }

        private Transform _subtreeRoot;

        private void RunAnalysis()
        {
            if (_target == null) return;

            // Stop any running incremental job
            _incrementalJob = null;
            _pendingReport = null;

            _analyzer.IncludeDefaultOverrides = _showDefaults;
            _analyzer.IncludeSceneOverrides = _showSceneOverrides;
            _analyzer.IncludeInternalProperties = _showInternalProps;

            if (_useIncremental)
            {
                _pendingReport = new AnalysisReport();
                _incrementalJob = _analyzer.AnalyzeIncremental(_target, _pendingReport, 300);
                // Note: incremental doesn't support subtree yet — full scan
                _progress = 0f;
                EditorApplication.update += PumpIncrementalJob;
            }
            else
            {
                _report = _analyzer.Analyze(_target, _subtreeRoot);
                _selectedGoIndex = _report.GameObjects.Count > 0 ? 0 : -1;
                _selectedConflicts.Clear();
                RebuildConflictList();
            }

            Repaint();
        }

        private void RunHierarchyAnalysis()
        {
            if (_target == null) return;

            // Stop any running jobs — hierarchy run supersedes both the
            // instance-mode incremental job and any in-flight hierarchy job.
            _incrementalJob = null;
            _pendingReport = null;
            if (_hierarchyJob != null)
            {
                _analyzer.AbortRun();
                _hierarchyJob = null;
                _pendingHierarchyReport = null;
                EditorApplication.update -= PumpHierarchyJob;
                EditorUtility.ClearProgressBar();
            }

            _analyzer.IncludeDefaultOverrides = _showDefaults;
            _analyzer.IncludeSceneOverrides = true; // hierarchy mode always includes scene
            _analyzer.IncludeInternalProperties = _showInternalProps;

            // Rough total for the progress bar — the analyzer itself will
            // add the root to its own list separately, so this is a lower
            // bound that stays useful even if it is off by one.
            _hierarchyInstancesTotal = CountNestedPrefabInstances(_target.transform);
            _pendingHierarchyReport = new AnalysisReport();
            _hierarchyJob = _analyzer.AnalyzeHierarchyIncremental(
                _target, _pendingHierarchyReport);
            _progress = 0f;

            EditorUtility.DisplayProgressBar(
                "Prefab Doctor",
                $"Analyzing hierarchy ({_hierarchyInstancesTotal} instances)...",
                0f);

            EditorApplication.update += PumpHierarchyJob;
            Repaint();
        }

        private void PumpHierarchyJob()
        {
            if (_hierarchyJob == null)
            {
                EditorApplication.update -= PumpHierarchyJob;
                EditorUtility.ClearProgressBar();
                return;
            }

            // DisplayCancelableProgressBar is cheap enough to call every
            // pump tick and is the only way to surface the Cancel button.
            if (EditorUtility.DisplayCancelableProgressBar(
                    "Prefab Doctor",
                    $"Analyzing hierarchy {_progress:P0} ({_hierarchyInstancesTotal} instances)",
                    _progress))
            {
                _analyzer.AbortRun();
                _hierarchyJob = null;
                _pendingHierarchyReport = null;
                EditorApplication.update -= PumpHierarchyJob;
                EditorUtility.ClearProgressBar();
                Debug.Log("[Prefab Doctor] Hierarchy analysis cancelled by user");
                Repaint();
                return;
            }

            // Advance the enumerator for up to ~16ms — one editor frame
            // worth of work — then yield back to the editor so the UI
            // stays responsive and the Cancel button stays clickable.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 16)
            {
                if (!_hierarchyJob.MoveNext())
                {
                    _report = _pendingHierarchyReport;
                    _selectedGoIndex = _report.GameObjects.Count > 0 ? 0 : -1;
                    _selectedConflicts.Clear();

                    // On a hierarchy run, pick the narrowest actionable
                    // filter the data supports, so the user is not hit
                    // with 300k rows of lightmap / network noise by
                    // default. Preference order:
                    //   1. PingPong — classic A→B→A bugs, always rare.
                    //   2. GarbageOnly (Orphan + MultiOverride) — broken
                    //      or redundant overrides, the "clean me" bucket.
                    //   3. ConflictsOnly — whatever is left that is not
                    //      insignificant.
                    // The user can widen via the dropdown in one click.
                    _filterMode = _report.TotalPingPong > 0
                        ? FilterMode.PingPongOnly
                        : (_report.TotalOrphan + _report.TotalMultiOverride) > 0
                            ? FilterMode.GarbageOnly
                            : FilterMode.ConflictsOnly;

                    _hierarchyJob = null;
                    _pendingHierarchyReport = null;
                    EditorApplication.update -= PumpHierarchyJob;
                    EditorUtility.ClearProgressBar();

                    Debug.Log(
                        $"[Prefab Doctor] Hierarchy: {_report.InstancesAnalyzed} instances, "
                        + $"{_report.TotalPingPong} ping-pong, "
                        + $"{_report.TotalMultiOverride} multi, "
                        + $"{_report.TotalInsignificant} insignificant, "
                        + $"{_report.AnalysisTimeMs:F0}ms");

                    RebuildConflictList();
                    Repaint();
                    return;
                }
                _progress = _hierarchyJob.Current;
            }
            Repaint();
        }

        private void DoCleanOrphansHierarchy()
        {
            if (_report == null || !_report.IsHierarchyMode) return;
            var roots = _report.HierarchyInstanceRoots;
            if (roots == null || roots.Count == 0)
            {
                EditorUtility.DisplayDialog("Prefab Doctor",
                    "No prefab instance roots recorded in the current report.",
                    "OK");
                return;
            }

            if (_report.TotalOrphan == 0)
            {
                EditorUtility.DisplayDialog("Prefab Doctor",
                    "No orphan overrides in the current report — nothing to clean.",
                    "OK");
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Clean Orphans (Hierarchy)",
                $"Remove orphan PropertyModifications from {roots.Count} prefab instance"
                + (roots.Count == 1 ? "" : "s") + " in the current hierarchy?\n\n"
                + $"Detected {_report.TotalOrphan} orphan conflicts across those instances.\n\n"
                + "Supports Undo (Ctrl+Z). The containing prefabs / scenes will be marked dirty.",
                "Clean",
                "Cancel");

            if (!confirmed) return;

            int removed = OverrideActions.CleanOrphansHierarchy(roots);
            Debug.Log(
                $"[Prefab Doctor] Hierarchy: cleaned {removed} orphan modifications "
                + $"across {roots.Count} prefab instances");

            // Re-run analysis to refresh the report with the updated state.
            RunHierarchyAnalysis();
        }

        private static int CountNestedPrefabInstances(Transform root)
        {
            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                    count++;
                count += CountNestedPrefabInstances(child);
            }
            return count;
        }

        private void PumpIncrementalJob()
        {
            if (_incrementalJob == null)
            {
                EditorApplication.update -= PumpIncrementalJob;
                return;
            }

            // Process several steps per editor frame
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 16) // ~1 frame budget
            {
                if (!_incrementalJob.MoveNext())
                {
                    // Done
                    _report = _pendingReport;
                    _selectedGoIndex = _report.GameObjects.Count > 0 ? 0 : -1;
                    _selectedConflicts.Clear();
                    _incrementalJob = null;
                    _pendingReport = null;
                    EditorApplication.update -= PumpIncrementalJob;
                    RebuildConflictList();
                    Repaint();
                    return;
                }
                _progress = _incrementalJob.Current;
            }
            Repaint();
        }

        private void OnEnable()
        {
            if (_target == null && Selection.activeGameObject != null &&
                PrefabUtility.IsPartOfPrefabInstance(Selection.activeGameObject))
            {
                _target = PrefabUtility.GetOutermostPrefabInstanceRoot(Selection.activeGameObject);
            }

            // Persistent update for scan panel pump
            EditorApplication.update += PumpScanJob;
        }

        private void OnDisable()
        {
            EditorApplication.update -= PumpIncrementalJob;
            EditorApplication.update -= PumpHierarchyJob;
            EditorApplication.update -= PumpScanJob;
            _incrementalJob = null;

            // Abort any in-flight hierarchy job so its per-run caches get
            // released. Also make sure the modal progress bar is dismissed —
            // otherwise closing the window mid-run could leave Unity with
            // a stuck progress overlay.
            if (_hierarchyJob != null)
            {
                _analyzer?.AbortRun();
                _hierarchyJob = null;
                _pendingHierarchyReport = null;
                EditorUtility.ClearProgressBar();
            }

            // Release any SerializedObject handles the analyzer kept across
            // an incremental run that was abandoned by closing the window.
            _analyzer?.ClearSerializedObjectCache();
            _scanPanel.OnDisable();
        }

        private void PumpScanJob()
        {
            if (!_scanPanel.IsScanning) return;
            _scanPanel.PumpScanJob();
            Repaint();
        }

        private List<GameObjectReport> GetFilteredGameObjects()
        {
            if (_report == null) return new List<GameObjectReport>();

            // Reference-equality compare: report is never mutated once built,
            // so if we're still looking at the same AnalysisReport instance
            // with the same FilterMode, the filtered list can't have changed.
            if (ReferenceEquals(_filteredGOCacheReport, _report)
                && _filteredGOCacheMode == _filterMode
                && _filteredGOCache != null)
            {
                return _filteredGOCache;
            }

            List<GameObjectReport> result = _filterMode switch
            {
                FilterMode.PingPongOnly => FilterGameObjects(static g => g.PingPongCount > 0),
                FilterMode.OrphansOnly => FilterGameObjects(static g => g.OrphanCount > 0),
                FilterMode.InsignificantOnly => FilterGameObjects(static g => g.InsignificantCount > 0),
                FilterMode.ConflictsOnly => FilterGameObjects(static g =>
                    g.PingPongCount > 0 || g.MultiOverrideCount > 0 || g.OrphanCount > 0),
                FilterMode.LightmapOnly => FilterByCategory(OverrideCategory.Lightmap),
                FilterMode.NetworkNoiseOnly => FilterByCategory(OverrideCategory.NetworkNoise),
                FilterMode.TransformOnly => FilterByCategory(OverrideCategory.Transform),
                FilterMode.GraphicsOnly => FilterByCategories(
                    OverrideCategory.Lightmap,
                    OverrideCategory.StaticFlags,
                    OverrideCategory.Material,
                    OverrideCategory.Transform),
                FilterMode.GarbageOnly => FilterGameObjects(static g =>
                    g.OrphanCount > 0 || g.MultiOverrideCount > 0),
                _ => _report.GameObjects
            };

            _filteredGOCache = result;
            _filteredGOCacheReport = _report;
            _filteredGOCacheMode = _filterMode;
            return result;
        }

        private List<GameObjectReport> FilterGameObjects(Func<GameObjectReport, bool> predicate)
        {
            var src = _report.GameObjects;
            var dst = new List<GameObjectReport>(src.Count);
            for (int i = 0; i < src.Count; i++)
                if (predicate(src[i])) dst.Add(src[i]);
            return dst;
        }

        private List<GameObjectReport> FilterByCategory(OverrideCategory category)
        {
            var src = _report.GameObjects;
            var dst = new List<GameObjectReport>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                var conflicts = src[i].Conflicts;
                for (int c = 0; c < conflicts.Count; c++)
                {
                    if (conflicts[c].Category == category)
                    {
                        dst.Add(src[i]);
                        break;
                    }
                }
            }
            return dst;
        }

        private List<GameObjectReport> FilterByCategories(params OverrideCategory[] categories)
        {
            var src = _report.GameObjects;
            var dst = new List<GameObjectReport>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                var conflicts = src[i].Conflicts;
                bool hit = false;
                for (int c = 0; c < conflicts.Count && !hit; c++)
                {
                    var cat = conflicts[c].Category;
                    for (int k = 0; k < categories.Length; k++)
                    {
                        if (cat == categories[k]) { hit = true; break; }
                    }
                }
                if (hit) dst.Add(src[i]);
            }
            return dst;
        }

        private bool PassesFilter(PropertyConflict conflict)
        {
            return _filterMode switch
            {
                FilterMode.PingPongOnly => conflict.Severity == ConflictSeverity.PingPong,
                FilterMode.OrphansOnly => conflict.Severity == ConflictSeverity.Orphan,
                FilterMode.InsignificantOnly => conflict.Severity == ConflictSeverity.Insignificant,
                FilterMode.ConflictsOnly => conflict.Severity != ConflictSeverity.Insignificant,
                FilterMode.LightmapOnly => conflict.Category == OverrideCategory.Lightmap,
                FilterMode.NetworkNoiseOnly => conflict.Category == OverrideCategory.NetworkNoise,
                FilterMode.TransformOnly => conflict.Category == OverrideCategory.Transform,
                FilterMode.GraphicsOnly =>
                    conflict.Category == OverrideCategory.Lightmap
                    || conflict.Category == OverrideCategory.StaticFlags
                    || conflict.Category == OverrideCategory.Material
                    || conflict.Category == OverrideCategory.Transform,
                FilterMode.GarbageOnly =>
                    conflict.Severity == ConflictSeverity.Orphan
                    || conflict.Severity == ConflictSeverity.MultiOverride,
                _ => true
            };
        }

        private static string TruncateValue(string value, int maxLen)
        {
            if (string.IsNullOrEmpty(value)) return "null";
            if (value.Length <= maxLen) return value;
            return value[..(maxLen - 1)] + "…";
        }

        // ── Selection tracking ─────────────────────────────────────

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject != null &&
                PrefabUtility.IsPartOfPrefabInstance(Selection.activeGameObject))
            {
                if (_target == null)
                {
                    _target = PrefabUtility.GetOutermostPrefabInstanceRoot(
                        Selection.activeGameObject);
                    Repaint();
                }
            }
        }

        private void DrawEmptyState()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (_incrementalJob != null)
            {
                // Progress bar during incremental analysis
                EditorGUILayout.BeginVertical(GUILayout.Width(300));
                EditorGUILayout.LabelField("Analyzing...", EditorStyles.centeredGreyMiniLabel);
                var rect = GUILayoutUtility.GetRect(300, 20);
                EditorGUI.ProgressBar(rect, _progress, $"{(_progress * 100):F0}%");
                EditorGUILayout.EndVertical();
            }
            else if (_target == null)
            {
                EditorGUILayout.LabelField(
                    "Select a prefab instance and click Analyze\nor drag it into the target field.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.Width(300));
            }
            else if (_report != null && _report.GameObjects.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No conflicts found.\n" +
                    (_report.IsComplete ? "Prefab is clean." : "Analysis incomplete."),
                    EditorStyles.centeredGreyMiniLabel, GUILayout.Width(300));
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }
    }
}
