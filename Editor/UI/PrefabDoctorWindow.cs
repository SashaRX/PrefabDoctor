using System;
using System.Collections.Generic;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using System.Linq;
using UnityEditor;
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

        // Auto-ping: when true, clicking a GO in the left panel pings it
        // in the Hierarchy/Scene view. On large scenes (hierarchy mode)
        // the ping + Selection change can freeze Unity. Toggle off to
        // navigate the report without scene-side lag.
        private bool _autoPing = true;

        // UI state
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

        // Cached category counts — a hierarchy run with 300k+ objects
        // turns into millions of operations per RefreshStatusBar if
        // recomputed on every repaint. Invalidated on report change.
        private Dictionary<OverrideCategory, int> _categoryCountsCache;
        private AnalysisReport _categoryCountsCacheReport;

        // ── UI Toolkit elements (Phase 2 — full port) ──────────────
        // Tab bar
        private Toolbar _tabToolbar;
        private ToolbarToggle _instanceTabToggle;
        private ToolbarToggle _scanTabToggle;
        private bool _muteTabToggleSync;

        // Top toolbar (instance tab)
        private Toolbar _topToolbar;
        private ObjectField _targetField;
        private ToolbarMenu _filterMenu;
        private ToolbarToggle _defaultsToggle;
        private ToolbarToggle _sceneToggle;
        private ToolbarToggle _internalToggle;
        private ToolbarButton _cleanOrphansButton;
        private ToolbarButton _cleanInsignificantButton;
        private ToolbarButton _copyReportButton;

        // Status bar
        private VisualElement _statusBar;
        private Label _statusChainLabel;
        private Label _statusPpLabel;
        private Label _statusMultiLabel;
        private Label _statusOrphanLabel;
        private Label _statusInsigLabel;
        private Label _statusLightmapLabel;
        private Label _statusNetLabel;
        private Label _statusFlagsLabel;
        private Label _statusOtherLabel;
        private Label _statusElapsedLabel;

        // Tab bodies
        private VisualElement _instanceTab;
        private VisualElement _scanTab;
        private TwoPaneSplitView _instanceSplit;

        // Left panel
        private VisualElement _leftPanel;
        private ListView _gameObjectListView;
        private bool _muteGoListSync;

        // Right panel
        private VisualElement _rightPanel;
        private VisualElement _conflictHeader;
        private Label _conflictHeaderLabel;
        private Button _conflictHeaderPingButton;
        private VisualElement _batchBar;
        private Label _batchCountLabel;
        private MultiColumnListView _conflictListView;

        // Empty state
        private VisualElement _emptyState;
        private Label _emptyStateLabel;
        private ProgressBar _emptyStateProgress;

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
            GarbageOnly,
            /// <summary>Auto-generated noise: Lightmap + NetworkNoise + StaticFlags.</summary>
            NoiseOnly,
            /// <summary>Everything that needs fixing: PingPong + Multi + Orphan.</summary>
            ActionableOnly
        }

        // ── Main Layout ────────────────────────────────────────────
        //
        // Phase 2: the Instance Analysis tab is now 100% UI Toolkit
        // (toolbar, status bar, left panel, right panel, empty state,
        // tab bar). The Project Scan tab is still drawn via a single
        // IMGUIContainer wrapping ProjectScanPanel.OnGUI — Phase 3
        // ports that too. The left-panel GameObject list is now a
        // virtualised ListView, dropping the old k_MaxVisibleGOs cap,
        // and the batch bar gained a "Select All Matching" action
        // that spans the full report (not just the current GO).

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;

            // Tab bar
            _tabToolbar = new Toolbar();
            _instanceTabToggle = new ToolbarToggle { text = "Instance Analysis" };
            _instanceTabToggle.style.flexGrow = 1;
            _instanceTabToggle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _instanceTabToggle.RegisterValueChangedCallback(evt => OnTabToggle(0, evt.newValue));
            _tabToolbar.Add(_instanceTabToggle);

            _scanTabToggle = new ToolbarToggle { text = "Project Scan" };
            _scanTabToggle.style.flexGrow = 1;
            _scanTabToggle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _scanTabToggle.RegisterValueChangedCallback(evt => OnTabToggle(1, evt.newValue));
            _tabToolbar.Add(_scanTabToggle);
            root.Add(_tabToolbar);

            // Instance Analysis tab body
            _instanceTab = new VisualElement();
            _instanceTab.style.flexGrow = 1;
            _instanceTab.style.flexDirection = FlexDirection.Column;
            root.Add(_instanceTab);

            _topToolbar = BuildTopToolbar();
            _instanceTab.Add(_topToolbar);

            _statusBar = BuildStatusBar();
            _instanceTab.Add(_statusBar);

            _emptyState = BuildEmptyState();
            _instanceTab.Add(_emptyState);

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

            _leftPanel = BuildLeftPanel();
            _instanceSplit.Add(_leftPanel);

            _rightPanel = BuildRightPanel();
            _instanceSplit.Add(_rightPanel);

            // Project Scan tab body — still IMGUI for Phase 2
            _scanTab = new VisualElement();
            _scanTab.style.flexGrow = 1;
            _scanTab.style.display = DisplayStyle.None;
            var scanImgui = new IMGUIContainer(() => _scanPanel.OnGUI());
            scanImgui.style.flexGrow = 1;
            _scanTab.Add(scanImgui);
            root.Add(_scanTab);

            // Initial state sync
            _instanceTabToggle.SetValueWithoutNotify(_activeTab == 0);
            _scanTabToggle.SetValueWithoutNotify(_activeTab == 1);
            RefreshTabVisibility();
            RefreshTopToolbar();
            RefreshStatusBar();
            RefreshEmptyState();
            RefreshLeftPanel();
            RebuildConflictList();
            UpdateBatchBar();
        }

        private void OnTabToggle(int tab, bool value)
        {
            if (_muteTabToggleSync) return;
            if (!value)
            {
                // Don't allow deselecting the active tab — force it back on.
                _muteTabToggleSync = true;
                try
                {
                    if (tab == 0) _instanceTabToggle.SetValueWithoutNotify(_activeTab == 0);
                    else _scanTabToggle.SetValueWithoutNotify(_activeTab == 1);
                }
                finally { _muteTabToggleSync = false; }
                return;
            }

            _activeTab = tab;
            _muteTabToggleSync = true;
            try
            {
                _instanceTabToggle.SetValueWithoutNotify(tab == 0);
                _scanTabToggle.SetValueWithoutNotify(tab == 1);
            }
            finally { _muteTabToggleSync = false; }
            RefreshTabVisibility();
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

        private void RefreshEmptyState()
        {
            bool haveReport = _report != null && _report.GameObjects.Count > 0;
            SetDisplay(_instanceSplit, haveReport);
            SetDisplay(_emptyState, !haveReport);

            if (_emptyStateLabel == null) return;

            if (_incrementalJob != null || _hierarchyJob != null)
            {
                _emptyStateLabel.text = "Analyzing…";
                SetDisplay(_emptyStateProgress, true);
                _emptyStateProgress.value = _progress * 100f;
                _emptyStateProgress.title = $"{_progress * 100f:F0}%";
            }
            else if (_target == null)
            {
                _emptyStateLabel.text =
                    "Select a prefab instance and click Analyze,\n"
                    + "or drag it into the target field above.";
                SetDisplay(_emptyStateProgress, false);
            }
            else if (_report != null && _report.GameObjects.Count == 0)
            {
                _emptyStateLabel.text = _report.IsComplete
                    ? "No conflicts found. Prefab is clean."
                    : "No conflicts found.\nAnalysis incomplete.";
                SetDisplay(_emptyStateProgress, false);
            }
            else
            {
                _emptyStateLabel.text = "Click Analyze to begin.";
                SetDisplay(_emptyStateProgress, false);
            }
        }

        // ── Top Toolbar (UI Toolkit) ───────────────────────────────

        private Toolbar BuildTopToolbar()
        {
            var toolbar = new Toolbar();
            toolbar.style.flexShrink = 0;

            _targetField = new ObjectField
            {
                objectType = typeof(GameObject),
                allowSceneObjects = true
            };
            _targetField.style.width = 220;
            _targetField.style.marginLeft = 2;
            _targetField.style.marginRight = 2;
            _targetField.SetValueWithoutNotify(_target);
            _targetField.RegisterValueChangedCallback(evt =>
            {
                _target = evt.newValue as GameObject;
                _report = null;
                _selectedConflicts.Clear();
                RefreshAfterReportChange();
            });
            toolbar.Add(_targetField);

            var selectionButton = new ToolbarButton(() =>
            {
                if (Selection.activeGameObject == null) return;
                _target = Selection.activeGameObject;
                _targetField.SetValueWithoutNotify(_target);
                _report = null;
                _selectedConflicts.Clear();
                RefreshAfterReportChange();
            })
            { text = "← Selection" };
            toolbar.Add(selectionButton);

            toolbar.Add(new ToolbarSpacer());

            var analyzeButton = new ToolbarButton(RunAnalysis) { text = "Analyze" };
            analyzeButton.style.color = new Color(0.5f, 0.9f, 0.5f);
            toolbar.Add(analyzeButton);

            var hierarchyButton = new ToolbarButton(RunHierarchyAnalysis)
            {
                text = "▼ Hierarchy"
            };
            hierarchyButton.style.color = new Color(0.45f, 0.7f, 1f);
            toolbar.Add(hierarchyButton);

            toolbar.Add(new ToolbarSpacer());

            // Filter menu — rebuild conflict list on change so the
            // MultiColumnListView picks up the new set. Selection
            // (by stable ConflictHandle) survives the rebuild.
            _filterMenu = new ToolbarMenu { text = FilterModeLabel(_filterMode) };
            _filterMenu.style.minWidth = 140;
            foreach (FilterMode fm in Enum.GetValues(typeof(FilterMode)))
            {
                var captured = fm;
                _filterMenu.menu.AppendAction(FilterModeLabel(captured),
                    _ =>
                    {
                        if (_filterMode == captured) return;
                        _filterMode = captured;
                        _filterMenu.text = FilterModeLabel(captured);
                        RefreshLeftPanel();
                        RebuildConflictList();
                    },
                    _ => _filterMode == captured
                        ? DropdownMenuAction.Status.Checked
                        : DropdownMenuAction.Status.Normal);
            }
            toolbar.Add(_filterMenu);

            var flexSpacer = new ToolbarSpacer { style = { flexGrow = 1 } };
            toolbar.Add(flexSpacer);

            _defaultsToggle = new ToolbarToggle { text = "Defaults", value = _showDefaults };
            _defaultsToggle.tooltip = "Include default overrides (m_Name, m_IsActive, parent).";
            _defaultsToggle.RegisterValueChangedCallback(evt => _showDefaults = evt.newValue);
            toolbar.Add(_defaultsToggle);

            _sceneToggle = new ToolbarToggle { text = "Scene", value = _showSceneOverrides };
            _sceneToggle.tooltip = "Include scene-level (instance) overrides.";
            _sceneToggle.RegisterValueChangedCallback(evt => _showSceneOverrides = evt.newValue);
            toolbar.Add(_sceneToggle);

            _internalToggle = new ToolbarToggle
            { text = "Internal", value = _showInternalProps };
            _internalToggle.tooltip = "Include internal / backing-field properties.";
            _internalToggle.RegisterValueChangedCallback(evt => _showInternalProps = evt.newValue);
            toolbar.Add(_internalToggle);

            var pingToggle = new ToolbarToggle { text = "Ping", value = _autoPing };
            pingToggle.tooltip = "Auto-ping scene object on left-panel click. "
                + "Disable in hierarchy mode if ping causes lag on large scenes.";
            pingToggle.RegisterValueChangedCallback(evt => _autoPing = evt.newValue);
            toolbar.Add(pingToggle);

            _cleanOrphansButton = new ToolbarButton(OnCleanOrphansClicked)
            { text = "Clean Orphans" };
            toolbar.Add(_cleanOrphansButton);

            _cleanInsignificantButton = new ToolbarButton(() =>
            {
                if (_report == null) return;
                int removed = OverrideActions.CleanInsignificant(_target, _report.Chain);
                Debug.Log($"[Prefab Doctor] Cleaned {removed} insignificant overrides");
                RunAnalysis();
            })
            { text = "Clean Insignificant" };
            toolbar.Add(_cleanInsignificantButton);

            _copyReportButton = new ToolbarButton(() =>
            {
                if (_report == null) return;
                string md = OverrideReportFormatter.ToMarkdown(_report);
                EditorGUIUtility.systemCopyBuffer = md;
                Debug.Log($"[Prefab Doctor] Copied report ({md.Length} chars) to clipboard");
            })
            { text = "Copy Report" };
            toolbar.Add(_copyReportButton);

            return toolbar;
        }

        private static string FilterModeLabel(FilterMode mode) => mode switch
        {
            FilterMode.ConflictsOnly => "Conflicts Only",
            FilterMode.AllOverrides => "All Overrides",
            FilterMode.PingPongOnly => "Ping-Pong Only",
            FilterMode.OrphansOnly => "Orphans Only",
            FilterMode.InsignificantOnly => "Insignificant Only",
            FilterMode.LightmapOnly => "Lightmap Only",
            FilterMode.NetworkNoiseOnly => "Network Noise Only",
            FilterMode.GraphicsOnly => "Graphics Only",
            FilterMode.TransformOnly => "Transform Only",
            FilterMode.GarbageOnly => "Garbage Only",
            FilterMode.NoiseOnly => "Noise Only",
            FilterMode.ActionableOnly => "Actionable Only",
            _ => mode.ToString()
        };

        private void OnCleanOrphansClicked()
        {
            if (_report == null) return;
            bool hierarchy = _report.IsHierarchyMode
                && _report.HierarchyInstanceRoots != null
                && _report.HierarchyInstanceRoots.Count > 0;
            if (hierarchy)
            {
                DoCleanOrphansHierarchy();
            }
            else
            {
                int removed = OverrideActions.CleanOrphans(_target);
                Debug.Log($"[Prefab Doctor] Cleaned {removed} orphaned overrides");
                RunAnalysis();
            }
        }

        private void RefreshTopToolbar()
        {
            if (_topToolbar == null) return;
            bool haveReport = _report != null;
            bool hierarchy = haveReport && _report.IsHierarchyMode
                && _report.HierarchyInstanceRoots != null
                && _report.HierarchyInstanceRoots.Count > 0;

            _cleanOrphansButton.SetEnabled(haveReport);
            _cleanOrphansButton.text = hierarchy
                ? $"Clean Orphans ({_report.HierarchyInstanceRoots.Count})"
                : "Clean Orphans";
            _cleanInsignificantButton.SetEnabled(haveReport);
            _copyReportButton.SetEnabled(haveReport);

            if (_targetField != null && _targetField.value != (UnityEngine.Object)_target)
                _targetField.SetValueWithoutNotify(_target);
        }

        // ── Status Bar (UI Toolkit) ────────────────────────────────

        private VisualElement BuildStatusBar()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.flexShrink = 0;
            bar.style.paddingLeft = 6;
            bar.style.paddingRight = 6;
            bar.style.paddingTop = 2;
            bar.style.paddingBottom = 2;
            bar.style.borderBottomWidth = 1;
            bar.style.borderBottomColor = new Color(0f, 0f, 0f, 0.35f);
            bar.style.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 0.45f);

            _statusChainLabel = new Label("(no report)");
            _statusChainLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
            _statusChainLabel.style.flexGrow = 1;
            _statusChainLabel.style.color = new Color(0.75f, 0.75f, 0.75f);
            _statusChainLabel.style.overflow = Overflow.Hidden;
            _statusChainLabel.style.textOverflow = TextOverflow.Ellipsis;
            bar.Add(_statusChainLabel);

            _statusPpLabel = MakeBadgeLabel();
            bar.Add(_statusPpLabel);
            _statusMultiLabel = MakeBadgeLabel();
            bar.Add(_statusMultiLabel);
            _statusOrphanLabel = MakeBadgeLabel();
            bar.Add(_statusOrphanLabel);
            _statusInsigLabel = MakeBadgeLabel();
            bar.Add(_statusInsigLabel);

            var separator = new Label("│");
            separator.style.color = new Color(0.4f, 0.4f, 0.4f);
            separator.style.marginLeft = 6;
            separator.style.marginRight = 6;
            bar.Add(separator);

            _statusLightmapLabel = MakeBadgeLabel();
            bar.Add(_statusLightmapLabel);
            _statusNetLabel = MakeBadgeLabel();
            bar.Add(_statusNetLabel);
            _statusFlagsLabel = MakeBadgeLabel();
            bar.Add(_statusFlagsLabel);
            _statusOtherLabel = MakeBadgeLabel();
            bar.Add(_statusOtherLabel);

            _statusElapsedLabel = new Label("");
            _statusElapsedLabel.style.marginLeft = 8;
            _statusElapsedLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            _statusElapsedLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            bar.Add(_statusElapsedLabel);

            return bar;
        }

        private static Label MakeBadgeLabel()
        {
            var lbl = new Label
            {
                style =
                {
                    marginLeft = 4,
                    marginRight = 4,
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
            return lbl;
        }

        private void RefreshStatusBar()
        {
            if (_statusBar == null) return;

            if (_report == null)
            {
                _statusChainLabel.text = "(no report)";
                _statusPpLabel.text = "";
                _statusMultiLabel.text = "";
                _statusOrphanLabel.text = "";
                _statusInsigLabel.text = "";
                _statusLightmapLabel.text = "";
                _statusNetLabel.text = "";
                _statusFlagsLabel.text = "";
                _statusOtherLabel.text = "";
                _statusElapsedLabel.text = "";
                return;
            }

            _statusChainLabel.text = _report.IsHierarchyMode
                ? $"HIERARCHY · {_report.InstancesAnalyzed} instances · "
                    + $"{_report.GameObjects.Count} objects with overrides"
                : "Chain: " + string.Join(" → ",
                    _report.Chain.Select(l => l.IsSceneInstance ? "[Scene]"
                        : System.IO.Path.GetFileNameWithoutExtension(l.AssetPath)));

            SetBadge(_statusPpLabel, "PP", _report.TotalPingPong,
                new Color(1f, 0.35f, 0.35f));
            SetBadge(_statusMultiLabel, "Multi", _report.TotalMultiOverride,
                new Color(1f, 0.75f, 0.2f));
            SetBadge(_statusOrphanLabel, "Orphan", _report.TotalOrphan,
                new Color(0.65f, 0.65f, 0.65f));
            SetBadge(_statusInsigLabel, "Insig", _report.TotalInsignificant,
                new Color(0.5f, 0.8f, 1f));

            var cats = GetCategoryCounts();
            SetBadge(_statusLightmapLabel, "Lightmap", cats[OverrideCategory.Lightmap],
                new Color(1f, 0.9f, 0.3f));
            SetBadge(_statusNetLabel, "Net", cats[OverrideCategory.NetworkNoise],
                new Color(0.45f, 0.9f, 0.7f));
            SetBadge(_statusFlagsLabel, "Flags", cats[OverrideCategory.StaticFlags],
                new Color(0.82f, 0.7f, 1f));
            int other = cats[OverrideCategory.General]
                + cats[OverrideCategory.Transform]
                + cats[OverrideCategory.Name]
                + cats[OverrideCategory.Material];
            SetBadge(_statusOtherLabel, "Other", other,
                new Color(0.75f, 0.75f, 0.75f));

            _statusElapsedLabel.text = $"{_report.AnalysisTimeMs:F0}ms";
        }

        private static void SetBadge(Label label, string name, int count, Color activeColor)
        {
            label.text = $"{name}:{count}";
            label.style.color = count > 0
                ? activeColor
                : new Color(0.45f, 0.45f, 0.45f);
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

        // ── Left Panel: Virtualised ListView ───────────────────────

        private VisualElement BuildLeftPanel()
        {
            var panel = new VisualElement();
            panel.style.flexGrow = 1;
            panel.style.flexDirection = FlexDirection.Column;

            _gameObjectListView = new ListView
            {
                fixedItemHeight = 20,
                selectionType = SelectionType.Single,
                reorderable = false,
                showBorder = true,
                showBoundCollectionSize = false,
                showFoldoutHeader = false,
                showAlternatingRowBackgrounds = AlternatingRowBackground.None,
                horizontalScrollingEnabled = false,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                viewDataKey = "prefab-doctor-go-list",
                makeItem = MakeGameObjectRow,
                bindItem = BindGameObjectRow
            };
            _gameObjectListView.style.flexGrow = 1;
            _gameObjectListView.selectionChanged += OnGameObjectListSelectionChanged;
            panel.Add(_gameObjectListView);

            return panel;
        }

        private VisualElement MakeGameObjectRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 4;
            row.style.paddingRight = 4;

            var dot = new VisualElement();
            dot.name = "dot";
            dot.style.width = 10;
            dot.style.height = 10;
            dot.style.marginRight = 6;
            dot.style.borderTopLeftRadius = 5;
            dot.style.borderTopRightRadius = 5;
            dot.style.borderBottomLeftRadius = 5;
            dot.style.borderBottomRightRadius = 5;
            dot.style.flexShrink = 0;
            row.Add(dot);

            var name = new Label();
            name.name = "name";
            name.style.flexGrow = 1;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            row.Add(name);

            var counts = new Label();
            counts.name = "counts";
            counts.style.marginLeft = 4;
            counts.style.color = new Color(0.65f, 0.65f, 0.65f);
            counts.style.flexShrink = 0;
            row.Add(counts);

            return row;
        }

        private void BindGameObjectRow(VisualElement row, int index)
        {
            var filtered = GetFilteredGameObjects();
            if (index < 0 || index >= filtered.Count) return;
            var goReport = filtered[index];

            var dot = row.Q<VisualElement>("dot");
            var nameLabel = row.Q<Label>("name");
            var countsLabel = row.Q<Label>("counts");

            Color dotColor;
            if (goReport.PingPongCount > 0) dotColor = new Color(1f, 0.3f, 0.3f);
            else if (goReport.MultiOverrideCount > 0) dotColor = new Color(1f, 0.75f, 0.2f);
            else if (goReport.OrphanCount > 0) dotColor = new Color(0.6f, 0.6f, 0.6f);
            else dotColor = new Color(0.5f, 0.8f, 1f);
            dot.style.backgroundColor = dotColor;

            int slash = goReport.RelativePath.LastIndexOf('/');
            nameLabel.text = slash >= 0
                ? goReport.RelativePath[(slash + 1)..]
                : goReport.RelativePath;
            nameLabel.tooltip = goReport.RelativePath;

            var sb = new System.Text.StringBuilder();
            if (goReport.PingPongCount > 0) sb.Append("P:").Append(goReport.PingPongCount).Append(' ');
            if (goReport.MultiOverrideCount > 0) sb.Append("M:").Append(goReport.MultiOverrideCount).Append(' ');
            if (goReport.OrphanCount > 0) sb.Append("O:").Append(goReport.OrphanCount).Append(' ');
            countsLabel.text = sb.ToString();
        }

        private void OnGameObjectListSelectionChanged(IEnumerable<object> _)
        {
            if (_muteGoListSync) return;
            int idx = _gameObjectListView.selectedIndex;
            if (idx == _selectedGoIndex) return;
            _selectedGoIndex = idx;

            RebuildConflictList();

            if (_autoPing)
            {
                var filtered = GetFilteredGameObjects();
                if (idx >= 0 && idx < filtered.Count)
                {
                    var goReport = filtered[idx];
                    var sceneGO = ResolveByRelativePath(goReport.RelativePath);
                    if (sceneGO != null)
                    {
                        EditorGUIUtility.PingObject(sceneGO);
                        Selection.activeGameObject = sceneGO;
                    }
                }
            }
        }

        private void RefreshLeftPanel()
        {
            if (_gameObjectListView == null) return;

            var filtered = GetFilteredGameObjects();
            _gameObjectListView.itemsSource = filtered;
            _gameObjectListView.RefreshItems();

            // Clamp & push selection without firing selectionChanged (which
            // would re-ping the scene every time).
            if (_selectedGoIndex >= filtered.Count) _selectedGoIndex = -1;
            _muteGoListSync = true;
            try
            {
                _gameObjectListView.SetSelectionWithoutNotify(
                    _selectedGoIndex >= 0 ? new[] { _selectedGoIndex } : System.Array.Empty<int>());
            }
            finally { _muteGoListSync = false; }
        }

        // ── Empty state (UI Toolkit) ───────────────────────────────

        private VisualElement BuildEmptyState()
        {
            var root = new VisualElement();
            root.style.flexGrow = 1;
            root.style.alignItems = Align.Center;
            root.style.justifyContent = Justify.Center;

            _emptyStateLabel = new Label("Click Analyze to begin.");
            _emptyStateLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            _emptyStateLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _emptyStateLabel.style.whiteSpace = WhiteSpace.Normal;
            _emptyStateLabel.style.maxWidth = 360;
            root.Add(_emptyStateLabel);

            _emptyStateProgress = new ProgressBar
            {
                lowValue = 0f,
                highValue = 100f,
                value = 0f,
                title = "0%"
            };
            _emptyStateProgress.style.width = 320;
            _emptyStateProgress.style.marginTop = 12;
            _emptyStateProgress.style.display = DisplayStyle.None;
            root.Add(_emptyStateProgress);

            return root;
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

            _conflictHeader = BuildConflictHeader();
            panel.Add(_conflictHeader);

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

        private VisualElement BuildConflictHeader()
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.flexShrink = 0;
            header.style.paddingLeft = 6;
            header.style.paddingRight = 6;
            header.style.paddingTop = 4;
            header.style.paddingBottom = 4;

            _conflictHeaderLabel = new Label();
            _conflictHeaderLabel.style.flexGrow = 1;
            _conflictHeaderLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _conflictHeaderLabel.style.overflow = Overflow.Hidden;
            _conflictHeaderLabel.style.textOverflow = TextOverflow.Ellipsis;
            _conflictHeaderLabel.style.whiteSpace = WhiteSpace.NoWrap;
            header.Add(_conflictHeaderLabel);

            _conflictHeaderPingButton = new Button(OnHeaderPingClicked) { text = "Ping" };
            _conflictHeaderPingButton.style.flexShrink = 0;
            _conflictHeaderPingButton.style.display = DisplayStyle.None;
            header.Add(_conflictHeaderPingButton);

            return header;
        }

        private void OnHeaderPingClicked()
        {
            if (_report == null || _selectedGoIndex < 0) return;
            var filtered = GetFilteredGameObjects();
            if (_selectedGoIndex >= filtered.Count) return;
            var go = filtered[_selectedGoIndex];
            if (go.Instance != null)
            {
                EditorGUIUtility.PingObject(go.Instance);
                Selection.activeGameObject = go.Instance;
            }
        }

        private void RefreshConflictHeader()
        {
            if (_conflictHeaderLabel == null) return;
            if (_report == null || _selectedGoIndex < 0)
            {
                _conflictHeaderLabel.text = "Select a GameObject on the left to view its overrides.";
                _conflictHeaderLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
                _conflictHeaderLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
                _conflictHeaderPingButton.style.display = DisplayStyle.None;
                return;
            }

            var filtered = GetFilteredGameObjects();
            if (_selectedGoIndex >= filtered.Count)
            {
                _conflictHeaderLabel.text = "(GameObject out of range)";
                _conflictHeaderLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
                _conflictHeaderLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
                _conflictHeaderPingButton.style.display = DisplayStyle.None;
                return;
            }

            var goReport = filtered[_selectedGoIndex];
            _conflictHeaderLabel.text = goReport.RelativePath;
            _conflictHeaderLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _conflictHeaderLabel.style.color = new Color(0.9f, 0.9f, 0.9f);
            _conflictHeaderPingButton.style.display =
                goReport.Instance != null ? DisplayStyle.Flex : DisplayStyle.None;
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
            _batchCountLabel.style.minWidth = 180;
            bar.Add(_batchCountLabel);

            // Scope: current GameObject's visible rows only.
            var selectVisible = new Button(SelectVisible) { text = "Select Visible" };
            selectVisible.tooltip = "Select every row currently displayed in the "
                + "right panel (the current GameObject, with the active filter).";
            bar.Add(selectVisible);

            // Scope: every conflict in the whole report matching the current
            // filter — spans all GameObjects at once.
            var selectAllMatching = new Button(SelectAllMatching) { text = "Select All Matching" };
            selectAllMatching.tooltip = "Select every conflict in the entire "
                + "report that matches the current filter. Use with "
                + "LightmapOnly / NetworkNoiseOnly / GarbageOnly for Level-wide cleanups.";
            selectAllMatching.style.color = new Color(0.55f, 0.85f, 1f);
            bar.Add(selectAllMatching);

            var selectNone = new Button(SelectNone) { text = "Select None" };
            selectNone.tooltip = "Clear all selections across all GameObjects.";
            bar.Add(selectNone);

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            bar.Add(spacer);

            var revert = new Button(RevertSelected) { text = "Revert Selected" };
            revert.tooltip = "Revert every selected conflict (cross-GameObject). "
                + "Hierarchy mode pins each conflict to its own nested "
                + "PrefabInstance owner. One Undo group for the whole batch.";
            revert.style.color = new Color(1f, 0.85f, 0.55f);
            bar.Add(revert);

            var copy = new Button(CopySelectedPropertyPaths) { text = "Copy Paths" };
            copy.tooltip = "Copy every selected conflict's property path to the clipboard.";
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
            RefreshConflictHeader();
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

        /// <summary>
        /// Adds every row currently materialised in the MCLV (the current
        /// GameObject's conflicts that pass the active filter) to
        /// <c>_selectedConflicts</c>. Other GameObjects' selections stay.
        /// </summary>
        private void SelectVisible()
        {
            foreach (var row in _conflictRows)
                _selectedConflicts.Add(row.Handle);
            PushSelectionToListView();
            UpdateBatchBar();
        }

        /// <summary>
        /// Report-wide Select All: walk every GameObjectReport and every
        /// PropertyConflict in the whole report, keep only those that pass
        /// the active filter, and add their handles to
        /// <c>_selectedConflicts</c>. Previous selection is preserved (Add
        /// semantics). This is the Level-wide cleanup entry point — set a
        /// category filter (e.g. LightmapOnly), press this, then Revert
        /// Selected to fix every matching conflict across every GameObject
        /// in one batch.
        /// </summary>
        private void SelectAllMatching()
        {
            if (_report == null) return;

            int added = 0;
            for (int gi = 0; gi < _report.GameObjects.Count; gi++)
            {
                var go = _report.GameObjects[gi];
                for (int ci = 0; ci < go.Conflicts.Count; ci++)
                {
                    if (!PassesFilter(go.Conflicts[ci])) continue;
                    if (_selectedConflicts.Add(new ConflictHandle(gi, ci)))
                        added++;
                }
            }

            Debug.Log($"[Prefab Doctor] Select All Matching: added {added} conflicts "
                + $"(filter: {_filterMode}); total selection: {_selectedConflicts.Count}");

            PushSelectionToListView();
            UpdateBatchBar();
        }

        /// <summary>
        /// Clears the entire cross-GameObject selection set.
        /// </summary>
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
                RefreshAfterReportChange();
            }

            Repaint();
        }

        /// <summary>
        /// Refresh every UI Toolkit element whose content depends on
        /// <c>_report</c>: top toolbar (Clean Orphans label), status bar
        /// (counts + chain), left panel (GameObject list items), empty
        /// state (visibility), and the right-panel conflict list
        /// (MCLV items source). Single entry point so nothing gets
        /// forgotten when a new report arrives.
        /// </summary>
        private void RefreshAfterReportChange()
        {
            RefreshTopToolbar();
            RefreshStatusBar();
            RefreshLeftPanel();
            RebuildConflictList();
            RefreshEmptyState();
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

            // Advance the enumerator for up to 200ms per pump tick.
            // The hierarchy scan runs behind a modal DisplayCancelable-
            // ProgressBar, so 60fps responsiveness is not needed — the
            // only UI the user interacts with is the Cancel button,
            // which we check at the top of every tick. 200ms gives
            // ~12x throughput vs the old 16ms budget while still keeping
            // Cancel latency under a quarter second.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 200)
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

                    RefreshAfterReportChange();
                    return;
                }
                _progress = _hierarchyJob.Current;
            }
            RefreshEmptyState();
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
                    RefreshAfterReportChange();
                    return;
                }
                _progress = _incrementalJob.Current;
            }
            RefreshEmptyState();
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
                FilterMode.NoiseOnly => FilterByCategories(
                    OverrideCategory.Lightmap,
                    OverrideCategory.NetworkNoise,
                    OverrideCategory.StaticFlags),
                FilterMode.ActionableOnly => FilterGameObjects(static g =>
                    g.PingPongCount > 0 || g.MultiOverrideCount > 0 || g.OrphanCount > 0),
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
                FilterMode.NoiseOnly =>
                    conflict.Category == OverrideCategory.Lightmap
                    || conflict.Category == OverrideCategory.NetworkNoise
                    || conflict.Category == OverrideCategory.StaticFlags,
                FilterMode.ActionableOnly =>
                    conflict.Severity == ConflictSeverity.PingPong
                    || conflict.Severity == ConflictSeverity.MultiOverride
                    || conflict.Severity == ConflictSeverity.Orphan,
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

    }
}
