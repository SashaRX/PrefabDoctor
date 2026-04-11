using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

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

        // ── Public interface ───────────────────────────────────────

        public bool IsScanning => _scanJob != null;

        public void OnGUI()
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
            while (sw.ElapsedMilliseconds < 16)
            {
                if (!_scanJob.MoveNext())
                {
                    _report = _pendingReport;
                    _scanJob = null;
                    _pendingReport = null;
                    _selected.Clear();
                    return;
                }
                _progress = _scanJob.Current;
            }
        }

        public void OnDisable()
        {
            _scanJob = null;
        }

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

            EditorGUILayout.EndHorizontal();
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
