using System;
using System.Collections.Generic;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

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
        private Vector2 _rightScroll;
        private int _selectedGoIndex = -1;
        private float _splitRatio = 0.3f;
        private bool _isDraggingSplit;

        // Selection for batch ops
        private HashSet<int> _selectedConflicts = new();

        // Draw error isolation (see OnGUI try/catch)
        private Exception _lastDrawError;
        private bool _loggedDrawError;

        // Cached filtered GameObject view. OnGUI can run many times per
        // second; without this cache each repaint re-walks the report and
        // allocates a fresh List via LINQ Where().ToList().
        private List<GameObjectReport> _filteredGOCache;
        private AnalysisReport _filteredGOCacheReport;
        private FilterMode _filteredGOCacheMode;

        private enum FilterMode
        {
            ConflictsOnly,
            AllOverrides,
            PingPongOnly,
            OrphansOnly,
            InsignificantOnly,
            LightmapOnly,
            NetworkNoiseOnly
        }

        // ── Main Layout ────────────────────────────────────────────

        private void OnGUI()
        {
            // If a previous draw threw, short-circuit to an error panel so IMGUI's
            // Begin/End stack cannot be corrupted by retrying the broken path.
            if (_lastDrawError != null)
            {
                DrawDrawErrorState();
                return;
            }

            try
            {
                // Tab bar
                _activeTab = GUILayout.Toolbar(_activeTab, s_TabNames, GUILayout.Height(22));

                if (_activeTab == 1)
                {
                    _scanPanel.OnGUI();
                    return;
                }

                // Instance Analysis tab
                DrawToolbar();
                DrawStatusBar();

                if (_report == null || _report.GameObjects.Count == 0)
                {
                    DrawEmptyState();
                    return;
                }

                // Simple horizontal split — no manual rect math
                float leftWidth = position.width * _splitRatio;

                EditorGUILayout.BeginHorizontal();

                // Left panel: GameObject tree
                _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll,
                    GUILayout.Width(leftWidth), GUILayout.ExpandHeight(true));
                DrawGameObjectList();
                EditorGUILayout.EndScrollView();

                // Right panel: Conflict table
                _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll,
                    GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                DrawConflictList();
                EditorGUILayout.EndScrollView();

                EditorGUILayout.EndHorizontal();
            }
            catch (Exception ex)
            {
                _lastDrawError = ex;
                if (!_loggedDrawError)
                {
                    _loggedDrawError = true;
                    Debug.LogError($"[Prefab Doctor] OnGUI failed: {ex}");
                }
                Repaint();
            }
        }

        private void DrawDrawErrorState()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "Prefab Doctor hit a draw error and paused the window to protect Unity's IMGUI layout.\n\n"
                + _lastDrawError.Message,
                MessageType.Error);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Retry", GUILayout.Width(80)))
            {
                _lastDrawError = null;
                _loggedDrawError = false;
                Repaint();
            }
            if (GUILayout.Button("Copy details", GUILayout.Width(110)))
            {
                EditorGUIUtility.systemCopyBuffer = _lastDrawError.ToString();
            }
            EditorGUILayout.EndHorizontal();
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

            // Filter mode
            _filterMode = (FilterMode)EditorGUILayout.EnumPopup(_filterMode,
                EditorStyles.toolbarDropDown, GUILayout.Width(130));

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
                if (GUILayout.Button("Clean Orphans", EditorStyles.toolbarButton, GUILayout.Width(90)))
                {
                    int removed = OverrideActions.CleanOrphans(_target);
                    Debug.Log($"[Prefab Doctor] Cleaned {removed} orphaned overrides");
                    RunAnalysis();
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

            var catCounts = CountByCategory(_report);
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

            for (int i = 0; i < filteredGOs.Count; i++)
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
                    _selectedConflicts.Clear();
                }

                GUILayout.FlexibleSpace();

                string counts = "";
                if (goReport.PingPongCount > 0) counts += $"P:{goReport.PingPongCount} ";
                if (goReport.MultiOverrideCount > 0) counts += $"M:{goReport.MultiOverrideCount} ";
                if (goReport.OrphanCount > 0) counts += $"O:{goReport.OrphanCount} ";
                GUILayout.Label(counts, EditorStyles.miniLabel);

                EditorGUILayout.EndHorizontal();
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

        // ── Right Panel: Conflict Table ────────────────────────────

        private void DrawConflictList()
        {
            var filteredGOs = GetFilteredGameObjects();
            if (_selectedGoIndex < 0 || _selectedGoIndex >= filteredGOs.Count)
            {
                EditorGUILayout.HelpBox("Select a GameObject on the left to view overrides.",
                    MessageType.Info);
                return;
            }

            var goReport = filteredGOs[_selectedGoIndex];

            // Header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(goReport.RelativePath, EditorStyles.boldLabel);
            if (goReport.Instance != null &&
                GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                EditorGUIUtility.PingObject(goReport.Instance);
                Selection.activeGameObject = goReport.Instance;
            }
            EditorGUILayout.EndHorizontal();

            // Batch action bar
            if (_selectedConflicts.Count > 0)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                GUILayout.Label($"{_selectedConflicts.Count} selected", EditorStyles.miniLabel);
                if (GUILayout.Button("Revert Selected", EditorStyles.miniButton, GUILayout.Width(100)))
                {
                    var toRevert = _selectedConflicts
                        .Select(idx => goReport.Conflicts[idx])
                        .ToList();
                    OverrideActions.BatchRevert(_target, toRevert);
                    RunAnalysis();
                }
                EditorGUILayout.EndHorizontal();
            }

            // Column headers
            EditorGUILayout.BeginHorizontal("box");
            GUILayout.Label("Sev", EditorStyles.miniBoldLabel, GUILayout.Width(30));
            GUILayout.Label("Component", EditorStyles.miniBoldLabel, GUILayout.Width(100));
            GUILayout.Label("Property", EditorStyles.miniBoldLabel, GUILayout.Width(160));
            GUILayout.Label("Values by depth", EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            // Rows
            for (int i = 0; i < goReport.Conflicts.Count; i++)
            {
                var conflict = goReport.Conflicts[i];

                // Apply filter mode
                if (!PassesFilter(conflict)) continue;

                Color rowColor = conflict.Severity switch
                {
                    ConflictSeverity.PingPong => new Color(1f, 0.3f, 0.3f, 0.15f),
                    ConflictSeverity.MultiOverride => new Color(1f, 0.7f, 0f, 0.1f),
                    ConflictSeverity.Orphan => new Color(0.5f, 0.5f, 0.5f, 0.1f),
                    ConflictSeverity.Insignificant => new Color(0.5f, 0.8f, 1f, 0.08f),
                    _ => Color.clear
                };

                var rowRect = EditorGUILayout.BeginHorizontal();
                EditorGUI.DrawRect(rowRect, rowColor);

                // Checkbox
                bool wasSelected = _selectedConflicts.Contains(i);
                bool isSelected = GUILayout.Toggle(wasSelected, "", GUILayout.Width(16));
                if (isSelected != wasSelected)
                {
                    if (isSelected) _selectedConflicts.Add(i);
                    else _selectedConflicts.Remove(i);
                }

                // Severity badge
                string sevLabel = conflict.Severity switch
                {
                    ConflictSeverity.PingPong => "PP",
                    ConflictSeverity.MultiOverride => "M",
                    ConflictSeverity.Orphan => "O",
                    ConflictSeverity.Insignificant => "~",
                    _ => "?"
                };
                GUILayout.Label(sevLabel, EditorStyles.miniBoldLabel, GUILayout.Width(30));

                // Component & Property
                GUILayout.Label(conflict.Key.ComponentType, EditorStyles.miniLabel,
                    GUILayout.Width(100));
                GUILayout.Label(conflict.Key.PropertyPath, EditorStyles.miniLabel,
                    GUILayout.Width(160));

                // Values — compact inline display
                string valuesStr = string.Join(" → ",
                    conflict.Overrides.Select(o =>
                    {
                        string v = TruncateValue(o.Value, 12);
                        return $"D{o.Depth}:{v}";
                    }));
                GUILayout.Label(valuesStr, EditorStyles.miniLabel);

                EditorGUILayout.EndHorizontal();

                // Context menu on right-click
                if (Event.current.type == EventType.ContextClick &&
                    rowRect.Contains(Event.current.mousePosition))
                {
                    ShowConflictContextMenu(conflict, i, goReport);
                    Event.current.Use();
                }
            }
        }

        // ── Context Menu ───────────────────────────────────────────

        private void ShowConflictContextMenu(PropertyConflict conflict, int index,
            GameObjectReport goReport)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Revert All (return to base)"), false, () =>
            {
                OverrideActions.RevertAll(_target, conflict);
                RunAnalysis();
            });

            menu.AddSeparator("");

            // "Keep only at depth N" for each depth that has an override
            foreach (var entry in conflict.Overrides)
            {
                var level = _report.Chain.FirstOrDefault(l => l.Depth == entry.Depth);
                string name = level.IsSceneInstance ? "Scene" :
                    System.IO.Path.GetFileNameWithoutExtension(level.AssetPath);
                int capturedDepth = entry.Depth;
                string capturedValue = TruncateValue(entry.Value, 20);

                menu.AddItem(
                    new GUIContent($"Keep only at D{capturedDepth} ({name}) = {capturedValue}"),
                    false, () =>
                    {
                        OverrideActions.KeepOnlyAtDepth(_target, conflict, capturedDepth);
                        RunAnalysis();
                    });
            }

            menu.AddSeparator("");

            // Select/deselect
            if (_selectedConflicts.Contains(index))
                menu.AddItem(new GUIContent("Deselect"), false, () => _selectedConflicts.Remove(index));
            else
                menu.AddItem(new GUIContent("Select"), false, () => _selectedConflicts.Add(index));

            // Copy info
            menu.AddItem(new GUIContent("Copy property path"), false, () =>
            {
                EditorGUIUtility.systemCopyBuffer = conflict.Key.PropertyPath;
            });

            menu.ShowAsContext();
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

                    // Hierarchy mode treats insignificant/noise as primary
                    // output — the default ConflictsOnly filter hides most
                    // of the signal here.
                    _filterMode = FilterMode.AllOverrides;

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

                    Repaint();
                    return;
                }
                _progress = _hierarchyJob.Current;
            }
            Repaint();
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
