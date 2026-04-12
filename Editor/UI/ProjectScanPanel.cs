using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Draws the "Project Scan" tab inside PrefabDoctorWindow.
    /// Separated to keep the main window file manageable.
    /// </summary>
    internal class ProjectScanPanel
    {
        // ── State ──────────────────────────────────────────────────
        private ProjectScanReport _report;
        private ProjectScanner _scanner = new();
        private IEnumerator<float> _scanJob;
        private ProjectScanReport _pendingReport;
        private float _progress;

        private Vector2 _scroll;
        private string _folderScope;
        private ScanFilterMode _filterMode = ScanFilterMode.AllIssues;
        private bool _auditFbx = true;
        private bool _checkMaterials = true;

        // Selection for batch ops
        private HashSet<int> _selected = new();

        private enum ScanFilterMode
        {
            AllIssues,
            FbxBased,
            Broken,
            MissingScripts,
            UnusedOverrides,
            BadMaterials
        }

        // ── UI Toolkit elements ───────────────────────────────────
        private VisualElement _root;
        private ToolbarMenu _scopeMenu;
        private ToolbarMenu _filterMenu;
        private ToolbarToggle _fbxToggle;
        private ToolbarToggle _matToggle;
        private VisualElement _statusBar;
        private Label[] _statusBadges;
        private Label _statusElapsed;
        private Label _statusScope;
        private VisualElement _emptyState;
        private Label _emptyLabel;
        private ProgressBar _progressBar;
        private MultiColumnListView _resultsListView;
        private List<PrefabScanResult> _filteredCache;

        // ── Public interface ───────────────────────────────────────

        public bool IsScanning => _scanJob != null;

        public VisualElement BuildRoot()
        {
            _root = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column } };

            _root.Add(BuildScanToolbar());
            _root.Add(BuildScanStatusBar());

            _emptyState = new VisualElement { style = { flexGrow = 1, alignItems = Align.Center, justifyContent = Justify.Center } };
            _emptyLabel = new Label("Click 'Scan Project' to find prefab health issues\nacross the entire project or a specific folder.") { style = { color = new Color(0.6f, 0.6f, 0.6f), unityTextAlign = TextAnchor.MiddleCenter, whiteSpace = WhiteSpace.Normal, maxWidth = 360 } };
            _emptyState.Add(_emptyLabel);
            _progressBar = new ProgressBar { lowValue = 0, highValue = 100, title = "0%" };
            _progressBar.style.width = 320;
            _progressBar.style.marginTop = 12;
            _progressBar.style.display = DisplayStyle.None;
            _emptyState.Add(_progressBar);
            _root.Add(_emptyState);

            _resultsListView = BuildResultsListView();
            _resultsListView.style.display = DisplayStyle.None;
            _root.Add(_resultsListView);

            RefreshVisibility();
            return _root;
        }

        public void OnGUI()
        {
            // Legacy — no longer called. BuildRoot() is used instead.
        }

        private void RefreshVisibility()
        {
            if (_root == null) return;
            bool scanning = _scanJob != null;
            bool hasReport = _report != null && _report.IsComplete;

            _emptyState.style.display = (!hasReport || scanning) ? DisplayStyle.Flex : DisplayStyle.None;
            _resultsListView.style.display = (hasReport && !scanning) ? DisplayStyle.Flex : DisplayStyle.None;
            _progressBar.style.display = scanning ? DisplayStyle.Flex : DisplayStyle.None;

            if (scanning)
            {
                _emptyLabel.text = "Scanning project…";
                _progressBar.value = _progress * 100f;
                _progressBar.title = $"{_progress * 100f:F0}%";
            }
            else if (!hasReport)
            {
                _emptyLabel.text = "Click 'Scan Project' to find prefab health issues\nacross the entire project or a specific folder.";
            }
        }

        private void OnScanComplete()
        {
            _selected.Clear();
            RefreshStatusBar();
            RebuildResultsList();
            RefreshVisibility();
        }

        public void OnGUILegacy()
        {
            DrawScanToolbar();
            DrawScanStatusBar();

            if (_scanJob != null)
            {
                DrawProgressBar();
                return;
            }

            if (_report == null || !_report.IsComplete)
            {
                DrawEmptyScanState();
                return;
            }

            DrawResultsTable();
        }

        public void PumpScanJob()
        {
            if (_scanJob == null) return;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 200)
            {
                if (!_scanJob.MoveNext())
                {
                    _report = _pendingReport;
                    _scanJob = null;
                    _pendingReport = null;
                    OnScanComplete();
                    return;
                }
                _progress = _scanJob.Current;
            }
            RefreshVisibility();
        }

        public void OnDisable()
        {
            _scanJob = null;
        }

        // ── UI Toolkit Builders ───────────────────────────────────

        private Toolbar BuildScanToolbar()
        {
            var tb = new Toolbar();

            _scopeMenu = new ToolbarMenu { text = _folderScope ?? "Entire Project" };
            _scopeMenu.style.minWidth = 160;
            _scopeMenu.menu.AppendAction("Entire Project", _ => { _folderScope = null; _scopeMenu.text = "Entire Project"; });
            _scopeMenu.menu.AppendSeparator();
            _scopeMenu.menu.AppendAction("Pick Folder…", _ =>
            {
                string path = EditorUtility.OpenFolderPanel("Scan Folder", "Assets", "");
                if (!string.IsNullOrEmpty(path))
                {
                    if (path.StartsWith(Application.dataPath))
                        path = "Assets" + path[Application.dataPath.Length..];
                    _folderScope = path;
                    _scopeMenu.text = path;
                }
            });
            tb.Add(_scopeMenu);

            var scanBtn = new ToolbarButton(() => { RunScan(); RefreshVisibility(); }) { text = "Scan Project" };
            scanBtn.style.color = new Color(0.4f, 0.75f, 1f);
            tb.Add(scanBtn);

            tb.Add(new ToolbarSpacer());

            _filterMenu = new ToolbarMenu { text = "All Issues" };
            _filterMenu.style.minWidth = 120;
            foreach (ScanFilterMode fm in System.Enum.GetValues(typeof(ScanFilterMode)))
            {
                var cap = fm;
                _filterMenu.menu.AppendAction(ScanFilterLabel(cap), _ =>
                {
                    _filterMode = cap;
                    _filterMenu.text = ScanFilterLabel(cap);
                    RebuildResultsList();
                }, _ => _filterMode == cap ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
            tb.Add(_filterMenu);

            tb.Add(new ToolbarSpacer { style = { flexGrow = 1 } });

            _fbxToggle = new ToolbarToggle { text = "FBX Audit", value = _auditFbx };
            _fbxToggle.RegisterValueChangedCallback(e => _auditFbx = e.newValue);
            tb.Add(_fbxToggle);

            _matToggle = new ToolbarToggle { text = "Materials", value = _checkMaterials };
            _matToggle.RegisterValueChangedCallback(e => _checkMaterials = e.newValue);
            tb.Add(_matToggle);

            var cleanBtn = new ToolbarButton(DoCleanAllUnused) { text = "Clean All Unused" };
            cleanBtn.style.color = new Color(1f, 0.75f, 0.3f);
            tb.Add(cleanBtn);

            var lodBtn = new ToolbarButton(DoNormaliseLodLightmapScale) { text = "Fix LOD Lightmap Scale" };
            lodBtn.style.color = new Color(0.3f, 0.85f, 0.85f);
            tb.Add(lodBtn);

            return tb;
        }

        private static string ScanFilterLabel(ScanFilterMode m) => m switch
        {
            ScanFilterMode.AllIssues => "All Issues",
            ScanFilterMode.FbxBased => "FBX Based",
            ScanFilterMode.Broken => "Broken",
            ScanFilterMode.MissingScripts => "Missing Scripts",
            ScanFilterMode.UnusedOverrides => "Unused Overrides",
            ScanFilterMode.BadMaterials => "Bad Materials",
            _ => m.ToString()
        };

        private VisualElement BuildScanStatusBar()
        {
            _statusBar = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0, paddingLeft = 6, paddingRight = 6, paddingTop = 2, paddingBottom = 2, borderBottomWidth = 1, borderBottomColor = new Color(0, 0, 0, 0.35f), backgroundColor = new Color(0.16f, 0.16f, 0.16f, 0.45f) } };

            _statusScope = new Label("(no scan)") { style = { flexGrow = 1, color = new Color(0.75f, 0.75f, 0.75f) } };
            _statusBar.Add(_statusScope);

            string[] badgeNames = { "Broken", "Missing", "FBX", "Unused", "Mat" };
            Color[] badgeColors = { Color.red, new Color(1, 0.4f, 0), new Color(1, 0.7f, 0), new Color(0.5f, 0.8f, 1), new Color(1, 0.5f, 1) };
            _statusBadges = new Label[badgeNames.Length];
            for (int i = 0; i < badgeNames.Length; i++)
            {
                _statusBadges[i] = new Label { style = { marginLeft = 4, marginRight = 4, unityFontStyleAndWeight = FontStyle.Bold, color = new Color(0.45f, 0.45f, 0.45f) } };
                _statusBar.Add(_statusBadges[i]);
            }

            _statusElapsed = new Label { style = { marginLeft = 8, color = new Color(0.6f, 0.6f, 0.6f), unityFontStyleAndWeight = FontStyle.Italic } };
            _statusBar.Add(_statusElapsed);

            return _statusBar;
        }

        private void RefreshStatusBar()
        {
            if (_statusBar == null || _report == null || !_report.IsComplete) return;
            _statusScope.text = $"{_report.ScanScope} — {_report.TotalPrefabs} prefabs scanned";

            int[] counts = { _report.Broken, _report.MissingScripts, _report.FbxWithoutWrapper, _report.UnusedOverrides, _report.BadMaterialCount };
            string[] names = { "Broken", "Missing", "FBX", "Unused", "Mat" };
            Color[] active = { Color.red, new Color(1, 0.4f, 0), new Color(1, 0.7f, 0), new Color(0.5f, 0.8f, 1), new Color(1, 0.5f, 1) };
            for (int i = 0; i < _statusBadges.Length; i++)
            {
                _statusBadges[i].text = $"{names[i]}:{counts[i]}";
                _statusBadges[i].style.color = counts[i] > 0 ? active[i] : new Color(0.45f, 0.45f, 0.45f);
            }
            _statusElapsed.text = $"{_report.ScanTimeMs:F0}ms";
        }

        private MultiColumnListView BuildResultsListView()
        {
            var cols = new Columns();

            var catCol = new Column { name = "cat", title = "Category", stretchable = false };
            catCol.width = 110; catCol.makeCell = () => MakeScanLabel(); catCol.bindCell = BindCatCell;
            cols.Add(catCol);

            var prefabCol = new Column { name = "prefab", title = "Prefab", stretchable = false };
            prefabCol.width = 200; prefabCol.makeCell = () => MakeScanLabel(); prefabCol.bindCell = BindPrefabCell;
            cols.Add(prefabCol);

            var detailsCol = new Column { name = "details", title = "Details", stretchable = true };
            detailsCol.minWidth = 200; detailsCol.makeCell = () => MakeScanLabel(); detailsCol.bindCell = BindDetailsCell;
            cols.Add(detailsCol);

            var actionCol = new Column { name = "action", title = "Action", stretchable = false };
            actionCol.width = 120; actionCol.makeCell = MakeActionCell; actionCol.bindCell = BindActionCell;
            cols.Add(actionCol);

            var lv = new MultiColumnListView(cols)
            {
                selectionType = SelectionType.Multiple,
                fixedItemHeight = 22,
                reorderable = false,
                showBorder = true,
                showBoundCollectionSize = false,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                viewDataKey = "prefab-doctor-scan-results"
            };
            lv.style.flexGrow = 1;
            return lv;
        }

        private Label MakeScanLabel()
        {
            var lbl = new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, marginLeft = 4, marginRight = 4, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis, whiteSpace = WhiteSpace.NoWrap } };
            lbl.AddManipulator(new ContextualMenuManipulator(PopulateScanRowMenu));
            return lbl;
        }

        private VisualElement MakeActionCell()
        {
            var btn = new Button { style = { flexGrow = 1 } };
            btn.AddManipulator(new ContextualMenuManipulator(PopulateScanRowMenu));
            return btn;
        }

        private void BindCatCell(VisualElement el, int idx)
        {
            var lbl = (Label)el;
            if (idx < 0 || _filteredCache == null || idx >= _filteredCache.Count) { lbl.text = ""; return; }
            var r = _filteredCache[idx]; lbl.userData = idx;
            lbl.text = r.PrimaryCategory switch
            {
                PrefabHealthCategory.Broken => "BROKEN",
                PrefabHealthCategory.MissingScripts => "Missing Scripts",
                PrefabHealthCategory.FbxWithoutWrapper => "FBX (no wrap)",
                PrefabHealthCategory.FbxHasWrapper => "FBX (has wrap)",
                PrefabHealthCategory.BrokenReferences => "Broken Refs",
                PrefabHealthCategory.BadMaterials => "Bad Materials",
                PrefabHealthCategory.UnusedOverrides => "Unused Ovr",
                PrefabHealthCategory.FbxImportNoise => "FBX Import",
                _ => "?"
            };
        }

        private void BindPrefabCell(VisualElement el, int idx)
        {
            var lbl = (Label)el;
            if (idx < 0 || _filteredCache == null || idx >= _filteredCache.Count) { lbl.text = ""; return; }
            var r = _filteredCache[idx]; lbl.userData = idx;
            lbl.text = r.DisplayName;
            lbl.tooltip = r.AssetPath;
            lbl.style.color = new Color(0.4f, 0.7f, 1f);
            lbl.RegisterCallback<ClickEvent>(e =>
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(r.AssetPath);
                if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
            });
        }

        private void BindDetailsCell(VisualElement el, int idx)
        {
            var lbl = (Label)el;
            if (idx < 0 || _filteredCache == null || idx >= _filteredCache.Count) { lbl.text = ""; return; }
            lbl.userData = idx;
            lbl.text = BuildDetailsString(_filteredCache[idx]);
        }

        private void BindActionCell(VisualElement el, int idx)
        {
            var btn = (Button)el;
            btn.clicked -= null; // can't easily unsubscribe lambdas
            if (idx < 0 || _filteredCache == null || idx >= _filteredCache.Count) { btn.text = ""; btn.SetEnabled(false); return; }
            var r = _filteredCache[idx]; btn.userData = idx; btn.SetEnabled(true);

            switch (r.PrimaryCategory)
            {
                case PrefabHealthCategory.FbxWithoutWrapper:
                    btn.text = "Create Wrapper";
                    btn.clickable = new Clickable(() => { ProjectScanActions.CreateFbxWrapper(r.BaseFbxPath); RunScan(); OnScanComplete(); });
                    break;
                case PrefabHealthCategory.MissingScripts:
                    btn.text = "Remove Scripts";
                    btn.clickable = new Clickable(() => { ProjectScanActions.RemoveMissingScripts(r.AssetPath); RunScan(); OnScanComplete(); });
                    break;
                case PrefabHealthCategory.UnusedOverrides:
                case PrefabHealthCategory.BrokenReferences:
                    btn.text = "Clean Overrides";
                    btn.clickable = new Clickable(() => { ProjectScanActions.RemoveUnusedOverrides(r.AssetPath); RunScan(); OnScanComplete(); });
                    break;
                default:
                    btn.text = "—";
                    btn.SetEnabled(false);
                    break;
            }
        }

        private void PopulateScanRowMenu(ContextualMenuPopulateEvent evt)
        {
            if (!(evt.target is VisualElement ve) || !(ve.userData is int idx)) return;
            if (_filteredCache == null || idx < 0 || idx >= _filteredCache.Count) return;
            var r = _filteredCache[idx];

            evt.menu.AppendAction("Ping in Project", _ =>
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(r.AssetPath);
                if (obj != null) EditorGUIUtility.PingObject(obj);
            });
            evt.menu.AppendAction("Open Prefab", _ =>
                AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(r.AssetPath)));
            evt.menu.AppendAction("Copy Path", _ =>
                EditorGUIUtility.systemCopyBuffer = r.AssetPath);
        }

        private void RebuildResultsList()
        {
            if (_resultsListView == null) return;
            _filteredCache = GetFilteredResults();
            _resultsListView.itemsSource = _filteredCache;
            _resultsListView.RefreshItems();
            RefreshVisibility();
        }

        // ── IMGUI (legacy, kept for reference) ───────────────────

        // ── Toolbar ────────────────────────────────────────────────

        private void DrawScanToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Folder scope
            if (GUILayout.Button(_folderScope ?? "Entire Project",
                    EditorStyles.toolbarDropDown, GUILayout.Width(160)))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Entire Project"), _folderScope == null, () =>
                    _folderScope = null);
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Pick Folder..."), false, () =>
                {
                    string path = EditorUtility.OpenFolderPanel("Scan Folder", "Assets", "");
                    if (!string.IsNullOrEmpty(path))
                    {
                        // Convert to project-relative
                        if (path.StartsWith(Application.dataPath))
                            path = "Assets" + path[Application.dataPath.Length..];
                        _folderScope = path;
                    }
                });
                menu.ShowAsContext();
            }

            GUILayout.Space(4);

            // Scan button
            GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
            if (GUILayout.Button("Scan Project", EditorStyles.toolbarButton, GUILayout.Width(90)))
                RunScan();
            GUI.backgroundColor = Color.white;

            GUILayout.Space(8);

            // Filter
            _filterMode = (ScanFilterMode)EditorGUILayout.EnumPopup(_filterMode,
                EditorStyles.toolbarDropDown, GUILayout.Width(120));

            GUILayout.FlexibleSpace();

            // Toggles
            _auditFbx = GUILayout.Toggle(_auditFbx, "FBX Audit",
                EditorStyles.toolbarButton, GUILayout.Width(65));
            _checkMaterials = GUILayout.Toggle(_checkMaterials, "Materials",
                EditorStyles.toolbarButton, GUILayout.Width(60));

            GUILayout.Space(4);

            // Batch actions
            if (_report != null && _report.IsComplete)
            {
                if (_selected.Count > 0 && GUILayout.Button(
                        $"Batch ({_selected.Count})", EditorStyles.toolbarDropDown,
                        GUILayout.Width(80)))
                {
                    ShowBatchMenu();
                }
            }

            // Deep bulk cleanup — works on every .prefab in the current
            // folder scope, with or without a prior scan. This is the
            // "just clean my whole project" button users reach for after
            // Unity's Hierarchy right-click Remove Unused Overrides leaves
            // deep nested orphans behind.
            GUI.backgroundColor = new Color(1f, 0.75f, 0.3f);
            if (GUILayout.Button("Clean All Unused", EditorStyles.toolbarButton,
                    GUILayout.Width(120)))
            {
                DoCleanAllUnused();
            }
            GUI.backgroundColor = Color.white;

            // Canonical LOD lightmap scale cascade: write 0.5^lodIndex into
            // every prefab with a LODGroup, then strip stale
            // m_ScaleInLightmap mods from intermediate variants so the
            // leaf value is what propagates to scenes. See Task 9 in the
            // plan file for the full rationale.
            GUI.backgroundColor = new Color(0.3f, 0.85f, 0.85f);
            if (GUILayout.Button("Fix LOD Lightmap Scale", EditorStyles.toolbarButton,
                    GUILayout.Width(150)))
            {
                DoNormaliseLodLightmapScale();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndHorizontal();
        }

        private void DoNormaliseLodLightmapScale()
        {
            string scope = _folderScope ?? "Assets";

            // Count prefabs up front — cheap GUID search.
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { scope });
            int total = guids.Length;
            if (total == 0)
            {
                EditorUtility.DisplayDialog("Prefab Doctor",
                    $"No prefab assets found under '{scope}'.", "OK");
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Fix LOD Renderer Settings",
                $"About to normalise LOD renderer settings in {total} prefab files "
                + $"under '{scope}'.\n\n"
                + "Phase A — for each LODGroup:\n"
                + "  • m_ScaleInLightmap cascade: LOD0=1, LOD1=0.5, LOD2=0.25, …\n"
                + "  • Renderer settings consistency: LOD1+ mirrors LOD0\n"
                + "    (CastShadows, ReceiveShadows, LightProbeUsage,\n"
                + "     ReflectionProbeUsage, MotionVectors, etc.)\n\n"
                + "Phase B — strip every m_ScaleInLightmap PropertyModification "
                + "from nested PrefabInstance nodes across intermediate prefabs.\n\n"
                + "This rewrites prefab asset files on disk.\n"
                + "Recommended: commit the working tree to git first.\n\n"
                + "Continue?",
                "Fix",
                "Cancel");

            if (!confirmed) return;

            int phaseAWrites = 0;
            int phaseBStripped = 0;

            try
            {
                phaseAWrites = ProjectScanActions.NormaliseLodLightmapScaleInScope(
                    _folderScope,
                    (i, n, path) =>
                        EditorUtility.DisplayCancelableProgressBar(
                            "Prefab Doctor — LOD Cascade (1/2)",
                            $"{i + 1} / {n}  {System.IO.Path.GetFileName(path)}",
                            n > 0 ? (float)i / n : 0f));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            try
            {
                phaseBStripped = ProjectScanActions.StripLodLightmapScaleOverridesInScope(
                    _folderScope,
                    (i, n, path) =>
                        EditorUtility.DisplayCancelableProgressBar(
                            "Prefab Doctor — Strip Intermediate (2/2)",
                            $"{i + 1} / {n}  {System.IO.Path.GetFileName(path)}",
                            n > 0 ? (float)i / n : 0f));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorUtility.DisplayDialog(
                "Prefab Doctor",
                $"Phase A: normalised {phaseAWrites} renderer properties\n"
                + "(m_ScaleInLightmap cascade + LOD0→LODn settings sync).\n\n"
                + $"Phase B: stripped {phaseBStripped} m_ScaleInLightmap "
                + $"modifications from intermediate prefabs.\n\n"
                + $"Scope: '{scope}'.\n\n"
                + "Re-run Scan Project (or hierarchy analysis on your scene) "
                + "to see the updated state.",
                "OK");

            if (_report != null && _report.IsComplete)
                RunScan();
        }

        private void DoCleanAllUnused()
        {
            string scope = _folderScope ?? "Assets";

            // Count prefabs up front so the dialog can show an accurate
            // total — this is a cheap GUID search, not a full scan.
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { scope });
            int total = guids.Length;
            if (total == 0)
            {
                EditorUtility.DisplayDialog("Prefab Doctor",
                    $"No prefab assets found under '{scope}'.", "OK");
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Clean All Unused Overrides",
                $"About to walk {total} prefab files under '{scope}'.\n\n"
                + "For each file Prefab Doctor will open it in isolation, "
                + "collect every nested PrefabInstance, and remove unused "
                + "overrides via Unity's built-in API (same as the Hierarchy "
                + "right-click menu, but deep).\n\n"
                + "This rewrites prefab asset files on disk.\n"
                + "Recommended: commit the working tree to git first.\n\n"
                + "Continue?",
                "Clean",
                "Cancel");

            if (!confirmed) return;

            int removed = 0;
            try
            {
                removed = ProjectScanActions.CleanAllUnusedOverridesInScope(
                    _folderScope,
                    (i, n, path) =>
                    {
                        // Returning true cancels the loop.
                        return EditorUtility.DisplayCancelableProgressBar(
                            "Prefab Doctor — Clean All Unused",
                            $"{i + 1} / {n}  {System.IO.Path.GetFileName(path)}",
                            n > 0 ? (float)i / n : 0f);
                    });
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorUtility.DisplayDialog(
                "Prefab Doctor",
                $"Removed {removed} unused override modifications across "
                + $"{total} prefab files under '{scope}'.\n\n"
                + "Re-run Scan Project to see the updated state.",
                "OK");

            // Auto-refresh scan if we have a report
            if (_report != null && _report.IsComplete)
                RunScan();
        }

        // ── Status bar ─────────────────────────────────────────────

        private void DrawScanStatusBar()
        {
            if (_report == null || !_report.IsComplete) return;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            GUILayout.Label($"{_report.ScanScope} — {_report.TotalPrefabs} prefabs scanned",
                EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            DrawScanBadge($"Broken:{_report.Broken}", Color.red, _report.Broken > 0);
            DrawScanBadge($"Missing:{_report.MissingScripts}", new Color(1f, 0.4f, 0f),
                _report.MissingScripts > 0);
            DrawScanBadge($"FBX:{_report.FbxWithoutWrapper}", new Color(1f, 0.7f, 0f),
                _report.FbxWithoutWrapper > 0);
            DrawScanBadge($"Unused:{_report.UnusedOverrides}", new Color(0.5f, 0.8f, 1f),
                _report.UnusedOverrides > 0);
            DrawScanBadge($"Mat:{_report.BadMaterialCount}", new Color(1f, 0.5f, 1f),
                _report.BadMaterialCount > 0);

            GUILayout.Label($"  {_report.ScanTimeMs:F0}ms", EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawScanBadge(string text, Color color, bool active)
        {
            var prev = GUI.contentColor;
            GUI.contentColor = active ? color : new Color(0.5f, 0.5f, 0.5f);
            GUILayout.Label(text, EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(false));
            GUI.contentColor = prev;
        }

        // ── Results table ──────────────────────────────────────────

        private void DrawResultsTable()
        {
            // Header
            EditorGUILayout.BeginHorizontal("box");
            GUILayout.Toggle(false, "", GUILayout.Width(16));
            GUILayout.Label("Category", EditorStyles.miniBoldLabel, GUILayout.Width(110));
            GUILayout.Label("Prefab", EditorStyles.miniBoldLabel, GUILayout.Width(200));
            GUILayout.Label("Details", EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label("Action", EditorStyles.miniBoldLabel, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            var filtered = GetFilteredResults();
            for (int i = 0; i < filtered.Count; i++)
            {
                var r = filtered[i];
                DrawResultRow(r, i);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawResultRow(PrefabScanResult r, int index)
        {
            Color rowColor = r.PrimaryCategory switch
            {
                PrefabHealthCategory.Broken => new Color(1f, 0.2f, 0.2f, 0.15f),
                PrefabHealthCategory.MissingScripts => new Color(1f, 0.4f, 0f, 0.12f),
                PrefabHealthCategory.FbxWithoutWrapper => new Color(1f, 0.7f, 0f, 0.1f),
                PrefabHealthCategory.BrokenReferences => new Color(1f, 0.5f, 0.5f, 0.1f),
                PrefabHealthCategory.BadMaterials => new Color(1f, 0.5f, 1f, 0.08f),
                PrefabHealthCategory.UnusedOverrides => new Color(0.5f, 0.8f, 1f, 0.08f),
                PrefabHealthCategory.FbxImportNoise => new Color(0.8f, 0.8f, 0.5f, 0.08f),
                PrefabHealthCategory.FbxHasWrapper => new Color(0.5f, 1f, 0.5f, 0.08f),
                _ => Color.clear
            };

            var rowRect = EditorGUILayout.BeginHorizontal();
            EditorGUI.DrawRect(rowRect, rowColor);

            // Checkbox
            bool wasSel = _selected.Contains(index);
            bool isSel = GUILayout.Toggle(wasSel, "", GUILayout.Width(16));
            if (isSel != wasSel)
            {
                if (isSel) _selected.Add(index);
                else _selected.Remove(index);
            }

            // Category
            string catLabel = r.PrimaryCategory switch
            {
                PrefabHealthCategory.Broken => "BROKEN",
                PrefabHealthCategory.MissingScripts => "Missing Scripts",
                PrefabHealthCategory.FbxWithoutWrapper => "FBX (no wrap)",
                PrefabHealthCategory.FbxHasWrapper => "FBX (has wrap)",
                PrefabHealthCategory.BrokenReferences => "Broken Refs",
                PrefabHealthCategory.BadMaterials => "Bad Materials",
                PrefabHealthCategory.UnusedOverrides => "Unused Ovr",
                PrefabHealthCategory.FbxImportNoise => "FBX Import",
                _ => "?"
            };
            GUILayout.Label(catLabel, EditorStyles.miniLabel, GUILayout.Width(110));

            // Prefab name — clickable link style
            var linkStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.4f, 0.7f, 1f) },
                hover = { textColor = new Color(0.5f, 0.8f, 1f) }
            };
            if (GUILayout.Button(r.DisplayName, linkStyle, GUILayout.Width(200)))
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(r.AssetPath);
                if (obj != null)
                {
                    EditorGUIUtility.PingObject(obj);
                    Selection.activeObject = obj;
                }
            }
            // Hover cursor
            var lastRect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(lastRect, MouseCursor.Link);

            // FBX source — clickable if exists
            if (!string.IsNullOrEmpty(r.BaseFbxPath))
            {
                var fbxStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(1f, 0.7f, 0.3f) }
                };
                string fbxName = System.IO.Path.GetFileName(r.BaseFbxPath);
                if (GUILayout.Button(fbxName, fbxStyle, GUILayout.Width(120)))
                {
                    var fbx = AssetDatabase.LoadMainAssetAtPath(r.BaseFbxPath);
                    if (fbx != null)
                    {
                        EditorGUIUtility.PingObject(fbx);
                        Selection.activeObject = fbx;
                    }
                }
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            }

            // Details
            string details = BuildDetailsString(r);
            GUILayout.Label(details, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

            // Action button
            DrawActionButton(r);

            EditorGUILayout.EndHorizontal();

            // Context menu
            if (Event.current.type == EventType.ContextClick &&
                rowRect.Contains(Event.current.mousePosition))
            {
                ShowRowContextMenu(r);
                Event.current.Use();
            }
        }

        private void DrawActionButton(PrefabScanResult r)
        {
            switch (r.PrimaryCategory)
            {
                case PrefabHealthCategory.FbxWithoutWrapper:
                    if (GUILayout.Button("Create Wrapper", EditorStyles.miniButton,
                            GUILayout.Width(120)))
                    {
                        string wrapper = ProjectScanActions.CreateFbxWrapper(r.BaseFbxPath);
                        if (wrapper != null) RunScan(); // refresh
                    }
                    break;

                case PrefabHealthCategory.FbxHasWrapper:
                    if (GUILayout.Button("Show Wrapper", EditorStyles.miniButton,
                            GUILayout.Width(120)))
                    {
                        if (r.ExistingWrapperPaths.Count > 0)
                        {
                            var obj = AssetDatabase.LoadMainAssetAtPath(r.ExistingWrapperPaths[0]);
                            if (obj != null) EditorGUIUtility.PingObject(obj);
                        }
                    }
                    break;

                case PrefabHealthCategory.MissingScripts:
                    if (GUILayout.Button("Remove Scripts", EditorStyles.miniButton,
                            GUILayout.Width(120)))
                    {
                        ProjectScanActions.RemoveMissingScripts(r.AssetPath);
                        RunScan();
                    }
                    break;

                case PrefabHealthCategory.UnusedOverrides:
                case PrefabHealthCategory.BrokenReferences:
                    if (GUILayout.Button("Clean Overrides", EditorStyles.miniButton,
                            GUILayout.Width(120)))
                    {
                        ProjectScanActions.RemoveUnusedOverrides(r.AssetPath);
                        RunScan();
                    }
                    break;

                case PrefabHealthCategory.BadMaterials:
                    if (GUILayout.Button("Select Prefab", EditorStyles.miniButton,
                            GUILayout.Width(120)))
                    {
                        Selection.activeObject =
                            AssetDatabase.LoadMainAssetAtPath(r.AssetPath);
                    }
                    break;

                default:
                    GUILayout.Label("", GUILayout.Width(120));
                    break;
            }
        }

        // ── Context menu ───────────────────────────────────────────

        private void ShowRowContextMenu(PrefabScanResult r)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Ping in Project"), false, () =>
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(r.AssetPath);
                if (obj != null) EditorGUIUtility.PingObject(obj);
            });

            menu.AddItem(new GUIContent("Open Prefab"), false, () =>
                AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(r.AssetPath)));

            menu.AddItem(new GUIContent("Copy Path"), false, () =>
                EditorGUIUtility.systemCopyBuffer = r.AssetPath);

            menu.AddSeparator("");

            if (r.BaseFbxPath != null)
            {
                menu.AddItem(new GUIContent("Ping FBX Source"), false, () =>
                {
                    var fbx = AssetDatabase.LoadMainAssetAtPath(r.BaseFbxPath);
                    if (fbx != null) EditorGUIUtility.PingObject(fbx);
                });

                if (r.PrimaryCategory == PrefabHealthCategory.FbxWithoutWrapper)
                {
                    menu.AddItem(new GUIContent("Create Wrapper"), false, () =>
                    {
                        ProjectScanActions.CreateFbxWrapper(r.BaseFbxPath);
                        RunScan();
                    });
                }
            }

            if (r.MissingScriptCount > 0)
            {
                menu.AddItem(new GUIContent(
                    $"Remove {r.MissingScriptCount} Missing Scripts"), false, () =>
                {
                    ProjectScanActions.RemoveMissingScripts(r.AssetPath);
                    RunScan();
                });
            }

            if (r.UnusedOverrideCount > 0 || r.BrokenReferenceCount > 0)
            {
                menu.AddItem(new GUIContent("Remove Unused Overrides"), false, () =>
                {
                    ProjectScanActions.RemoveUnusedOverrides(r.AssetPath);
                    RunScan();
                });
            }

            if (r.ImportIssues?.Count > 0)
            {
                menu.AddSeparator("");
                foreach (var issue in r.ImportIssues)
                {
                    menu.AddItem(new GUIContent($"FBX: {issue.Suggestion}"), false, () =>
                    {
                        // Open import settings
                        Selection.activeObject =
                            AssetImporter.GetAtPath(r.BaseFbxPath);
                    });
                }
            }

            menu.ShowAsContext();
        }

        private void ShowBatchMenu()
        {
            var menu = new GenericMenu();
            var filtered = GetFilteredResults();
            var selectedResults = _selected
                .Where(i => i < filtered.Count)
                .Select(i => filtered[i])
                .ToList();

            var withMissing = selectedResults.Where(r => r.MissingScriptCount > 0).ToList();
            if (withMissing.Count > 0)
            {
                menu.AddItem(new GUIContent(
                    $"Remove Missing Scripts ({withMissing.Count} prefabs)"), false, () =>
                {
                    ProjectScanActions.BatchRemoveMissingScripts(
                        withMissing.Select(r => r.AssetPath));
                    RunScan();
                });
            }

            var withUnused = selectedResults
                .Where(r => r.UnusedOverrideCount > 0 || r.BrokenReferenceCount > 0).ToList();
            if (withUnused.Count > 0)
            {
                menu.AddItem(new GUIContent(
                    $"Remove Unused Overrides ({withUnused.Count} prefabs)"), false, () =>
                {
                    ProjectScanActions.BatchRemoveUnusedOverrides(
                        withUnused.Select(r => r.AssetPath));
                    RunScan();
                });
            }

            var fbxNoWrap = selectedResults
                .Where(r => r.PrimaryCategory == PrefabHealthCategory.FbxWithoutWrapper &&
                            r.BaseFbxPath != null).ToList();
            if (fbxNoWrap.Count > 0)
            {
                var uniqueFbx = fbxNoWrap.Select(r => r.BaseFbxPath).Distinct().ToList();
                menu.AddItem(new GUIContent(
                    $"Create FBX Wrappers ({uniqueFbx.Count} models)"), false, () =>
                {
                    ProjectScanActions.BatchCreateWrappers(uniqueFbx);
                    RunScan();
                });
            }

            menu.ShowAsContext();
        }

        // ── Helpers ────────────────────────────────────────────────

        private void RunScan()
        {
            _scanJob = null;
            _pendingReport = new ProjectScanReport();
            _scanner.FolderScope = _folderScope;
            _scanner.AuditFbxImport = _auditFbx;
            _scanner.CheckMaterials = _checkMaterials;
            _scanJob = _scanner.ScanIncremental(_pendingReport, 15);
            _progress = 0f;
            _selected.Clear();
        }

        private List<PrefabScanResult> GetFilteredResults()
        {
            if (_report == null) return new List<PrefabScanResult>();

            return _filterMode switch
            {
                ScanFilterMode.FbxBased => _report.Results
                    .Where(r => r.AllCategories.Contains(PrefabHealthCategory.FbxWithoutWrapper) ||
                               r.AllCategories.Contains(PrefabHealthCategory.FbxHasWrapper) ||
                               r.AllCategories.Contains(PrefabHealthCategory.FbxImportNoise))
                    .ToList(),
                ScanFilterMode.Broken => _report.Results
                    .Where(r => r.AllCategories.Contains(PrefabHealthCategory.Broken)).ToList(),
                ScanFilterMode.MissingScripts => _report.Results
                    .Where(r => r.AllCategories.Contains(PrefabHealthCategory.MissingScripts))
                    .ToList(),
                ScanFilterMode.UnusedOverrides => _report.Results
                    .Where(r => r.AllCategories.Contains(PrefabHealthCategory.UnusedOverrides) ||
                               r.AllCategories.Contains(PrefabHealthCategory.BrokenReferences))
                    .ToList(),
                ScanFilterMode.BadMaterials => _report.Results
                    .Where(r => r.AllCategories.Contains(PrefabHealthCategory.BadMaterials))
                    .ToList(),
                _ => _report.Results
            };
        }

        private static string BuildDetailsString(PrefabScanResult r)
        {
            var parts = new List<string>();

            if (r.BaseFbxPath != null)
                parts.Add($"FBX: {Path.GetFileName(r.BaseFbxPath)}");
            if (r.MissingScriptCount > 0)
                parts.Add($"{r.MissingScriptCount} missing scripts");
            if (r.BrokenReferenceCount > 0)
                parts.Add($"{r.BrokenReferenceCount} broken refs");
            if (r.UnusedOverrideCount > 0)
                parts.Add($"{r.UnusedOverrideCount} unused ovr");
            if (r.BadMaterials?.Count > 0)
                parts.Add($"{r.BadMaterials.Count} bad mats");
            if (r.ImportIssues?.Count > 0)
                parts.Add($"{r.ImportIssues.Count} import issues");
            if (r.ExistingWrapperPaths?.Count > 0)
                parts.Add($"wrapper: {Path.GetFileNameWithoutExtension(r.ExistingWrapperPaths[0])}");
            if (r.OverrideCount > 0)
                parts.Add($"{r.OverrideCount} overrides total");

            return string.Join(" | ", parts);
        }

        private void DrawProgressBar()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(300));
            EditorGUILayout.LabelField("Scanning project...",
                EditorStyles.centeredGreyMiniLabel);
            var rect = GUILayoutUtility.GetRect(300, 20);
            EditorGUI.ProgressBar(rect, _progress, $"{(_progress * 100):F0}%");
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }

        private void DrawEmptyScanState()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                "Click 'Scan Project' to find prefab health issues\n" +
                "across the entire project or a specific folder.",
                EditorStyles.centeredGreyMiniLabel, GUILayout.Width(300));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }
    }
}
