using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SashaRX.PrefabDoctor
{
    /// <summary>
    /// Right-panel block for one prefab instance. Renders the instance
    /// header plus a GO → property pivot grid where each column is a
    /// depth level (only non-empty depths are shown). Cell context menu
    /// exposes per-depth revert/keep actions. All state changes bubble
    /// up through the supplied <see cref="Action"/> callback so the
    /// owning window can re-run analysis.
    /// </summary>
    internal sealed class InstanceBlockView : VisualElement
    {
        private readonly InstancePivotBlock _block;
        private readonly Action _onChanged;

        public InstanceBlockView(InstancePivotBlock block, Action onChanged)
        {
            _block = block ?? throw new ArgumentNullException(nameof(block));
            _onChanged = onChanged;

            AddToClassList("pd-instance-block");
            Add(BuildHeader());
            Add(BuildGrid());
        }

        // ── Header ─────────────────────────────────────────────────

        private VisualElement BuildHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("pd-instance-header");

            var title = new Label(_block.InstanceName);
            title.AddToClassList("pd-instance-title");
            title.RegisterCallback<ClickEvent>(_ =>
            {
                if (_block.InstanceRoot == null) return;
                EditorGUIUtility.PingObject(_block.InstanceRoot);
                Selection.activeGameObject = _block.InstanceRoot;
            });
            header.Add(title);

            int rowCount = 0;
            for (int i = 0; i < _block.GoGroups.Count; i++)
                rowCount += _block.GoGroups[i].Properties.Count;

            var summary = new Label(
                $"{_block.GoGroups.Count} GO · {rowCount} overrides · "
                + $"{_block.VisibleDepths.Count} depths");
            summary.AddToClassList("pd-instance-summary");
            header.Add(summary);

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            header.Add(spacer);

            var revertAll = new Button(RevertInstance) { text = "Revert Instance" };
            revertAll.tooltip = "Revert every override surfaced in this block.";
            revertAll.AddToClassList("pd-btn-revert");
            header.Add(revertAll);

            return header;
        }

        private void RevertInstance()
        {
            if (_block.InstanceRoot == null) return;

            var tasks = new List<(GameObject, PropertyConflict)>();
            foreach (var g in _block.GoGroups)
                foreach (var row in g.Properties)
                    tasks.Add((_block.InstanceRoot, row.Conflict));

            if (tasks.Count == 0) return;
            OverrideActions.BatchRevert(tasks);
            _onChanged?.Invoke();
        }

        // ── Pivot grid ─────────────────────────────────────────────

        private VisualElement BuildGrid()
        {
            var grid = new VisualElement();
            grid.AddToClassList("pd-pivot-grid");

            grid.Add(BuildHeaderRow());

            foreach (var group in _block.GoGroups)
                grid.Add(BuildGoSection(group));

            return grid;
        }

        private VisualElement BuildHeaderRow()
        {
            var row = new VisualElement();
            row.AddToClassList("pd-pivot-header-row");

            var label = new Label("GameObject / Property");
            label.AddToClassList("pd-pivot-head-label");
            row.Add(label);

            foreach (var depth in _block.VisibleDepths)
            {
                var cell = new VisualElement();
                cell.AddToClassList("pd-pivot-head-cell");

                var top = new Label($"D{depth}");
                top.AddToClassList("pd-pivot-head-depth");
                cell.Add(top);

                var asset = GetAssetName(depth);
                var bot = new Label(asset);
                bot.AddToClassList("pd-pivot-head-asset");
                bot.tooltip = GetAssetTooltip(depth);
                cell.Add(bot);

                row.Add(cell);
            }

            return row;
        }

        private string GetAssetName(int depth)
        {
            foreach (var l in _block.Chain)
            {
                if (l.Depth != depth) continue;
                if (l.IsSceneInstance) return "Scene";
                return string.IsNullOrEmpty(l.AssetPath)
                    ? "?"
                    : System.IO.Path.GetFileNameWithoutExtension(l.AssetPath);
            }
            return "?";
        }

        private string GetAssetTooltip(int depth)
        {
            foreach (var l in _block.Chain)
            {
                if (l.Depth != depth) continue;
                return l.IsSceneInstance
                    ? "Scene instance (depth 0)"
                    : string.IsNullOrEmpty(l.AssetPath) ? "(no asset path)" : l.AssetPath;
            }
            return $"Depth {depth}";
        }

        private VisualElement BuildGoSection(GoPropertyGroup group)
        {
            var foldout = new Foldout
            {
                text = $"{group.GoDisplayName}  ·  {group.Properties.Count} props",
                value = true
            };
            foldout.AddToClassList("pd-pivot-go-foldout");
            foldout.tooltip = group.GoPath;

            var headerToggle = foldout.Q<Toggle>();
            if (headerToggle != null)
                headerToggle.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    evt.menu.AppendAction("Ping GameObject", _ => PingGroup(group));
                    evt.menu.AppendAction("Copy path", _ =>
                        EditorGUIUtility.systemCopyBuffer = group.GoPath ?? string.Empty);
                }));

            foreach (var row in group.Properties)
                foldout.Add(BuildPropertyRow(group, row));

            return foldout;
        }

        private static void PingGroup(GoPropertyGroup group)
        {
            if (group.Go != null)
            {
                EditorGUIUtility.PingObject(group.Go);
                Selection.activeGameObject = group.Go;
            }
        }

        private VisualElement BuildPropertyRow(GoPropertyGroup group, PropertyRow row)
        {
            var line = new VisualElement();
            line.AddToClassList("pd-pivot-row");
            line.AddToClassList(SeverityClass(row.Conflict.Severity));

            var label = new Label(
                $"{row.Conflict.Key.ComponentType}  ·  {row.Conflict.Key.PropertyPath}");
            label.AddToClassList("pd-pivot-row-label");
            label.tooltip = $"{row.Conflict.Key.ComponentType}::{row.Conflict.Key.PropertyPath}\n"
                + $"Severity: {row.Conflict.Severity}  ·  Category: {row.Conflict.Category}";
            label.AddManipulator(new ContextualMenuManipulator(evt =>
                PopulateRowMenu(evt, group, row)));
            line.Add(label);

            foreach (var depth in _block.VisibleDepths)
            {
                line.Add(BuildValueCell(group, row, depth));
            }

            return line;
        }

        private VisualElement BuildValueCell(
            GoPropertyGroup group, PropertyRow row, int depth)
        {
            var cell = new Label();
            cell.AddToClassList("pd-pivot-cell");

            if (row.ByDepth.TryGetValue(depth, out var entry))
            {
                cell.text = Truncate(entry.Value, 16);
                cell.tooltip = $"D{depth} = {entry.Value ?? "null"}\n"
                    + (string.IsNullOrEmpty(entry.AssetPath) ? "(scene)" : entry.AssetPath);
                cell.AddToClassList("pd-pivot-cell--has");
                cell.userData = depth;
                cell.AddManipulator(new ContextualMenuManipulator(evt =>
                    PopulateCellMenu(evt, group, row, depth)));
            }
            else
            {
                cell.text = "—";
                cell.tooltip = $"No override at D{depth}";
                cell.AddToClassList("pd-pivot-cell--empty");
            }

            return cell;
        }

        private void PopulateRowMenu(
            ContextualMenuPopulateEvent evt, GoPropertyGroup group, PropertyRow row)
        {
            var conflict = row.Conflict;

            evt.menu.AppendAction("Ping GameObject", _ => PingGroup(group));
            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Revert All (return to base)", _ =>
            {
                if (_block.InstanceRoot == null) return;
                OverrideActions.BatchRevert(new[] { (_block.InstanceRoot, conflict) });
                _onChanged?.Invoke();
            });

            foreach (var depth in _block.VisibleDepths)
            {
                if (!row.ByDepth.TryGetValue(depth, out var entry)) continue;
                int capturedDepth = depth;
                string valuePreview = Truncate(entry.Value, 20);
                string name = GetAssetName(depth);
                evt.menu.AppendAction(
                    $"Keep only at D{capturedDepth} ({name}) = {valuePreview}",
                    _ =>
                    {
                        if (_block.InstanceRoot == null) return;
                        OverrideActions.KeepOnlyAtDepth(
                            _block.InstanceRoot, conflict, capturedDepth);
                        _onChanged?.Invoke();
                    });
            }

            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Copy property path", _ =>
                EditorGUIUtility.systemCopyBuffer = conflict.Key.PropertyPath ?? string.Empty);
        }

        private void PopulateCellMenu(
            ContextualMenuPopulateEvent evt,
            GoPropertyGroup group,
            PropertyRow row,
            int depth)
        {
            var conflict = row.Conflict;
            var hasEntry = row.ByDepth.TryGetValue(depth, out var entry);
            if (!hasEntry) return;

            int capturedDepth = depth;
            string capturedValue = entry.Value;
            string assetName = GetAssetName(depth);

            evt.menu.AppendAction($"Revert this depth (D{capturedDepth}, {assetName})", _ =>
            {
                if (_block.InstanceRoot == null) return;
                OverrideActions.RevertDepth(_block.InstanceRoot, conflict, capturedDepth);
                _onChanged?.Invoke();
            });

            evt.menu.AppendAction(
                $"Keep only this depth (D{capturedDepth}, {assetName})", _ =>
                {
                    if (_block.InstanceRoot == null) return;
                    OverrideActions.KeepOnlyAtDepth(
                        _block.InstanceRoot, conflict, capturedDepth);
                    _onChanged?.Invoke();
                });

            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Copy value", _ =>
                EditorGUIUtility.systemCopyBuffer = capturedValue ?? string.Empty);

            evt.menu.AppendAction("Ping source prefab", _ =>
            {
                foreach (var l in _block.Chain)
                {
                    if (l.Depth != capturedDepth) continue;
                    if (l.Root != null) EditorGUIUtility.PingObject(l.Root);
                    return;
                }
            });
        }

        // ── Helpers ────────────────────────────────────────────────

        private static string SeverityClass(ConflictSeverity severity) => severity switch
        {
            ConflictSeverity.PingPong => "pd-pivot-row--pp",
            ConflictSeverity.MultiOverride => "pd-pivot-row--multi",
            ConflictSeverity.Orphan => "pd-pivot-row--orphan",
            ConflictSeverity.Insignificant => "pd-pivot-row--insig",
            _ => string.Empty
        };

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "null";
            if (value.Length <= max) return value;
            return value[..(max - 1)] + "…";
        }
    }
}
