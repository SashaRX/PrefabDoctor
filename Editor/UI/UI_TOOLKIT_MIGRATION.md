# UI Toolkit Migration Notes — PrefabDoctorWindow

Research-only document. Nothing in this file is wired into the project yet; the
current window is pure IMGUI (`Editor/UI/PrefabDoctorWindow.cs`). Goal: catalog
what a UI Toolkit port would look like, and explicitly call out version gaps so
the runtime minimum can be raised deliberately.

## Unity version baseline

- `package.json` declares `"unity": "6000.0"` — Unity 6 is the minimum.
- Every UI Toolkit API referenced below is available on Unity 6, including
  `MultiColumnListView` (the headline virtualization win). No version gaps
  remain and no runtime shims are needed.

## Lifecycle: `CreateGUI()` vs `OnGUI()`

IMGUI runs every repaint via `OnGUI()`. UI Toolkit builds a persistent visual
tree once in `CreateGUI()` (called after `OnEnable`) and then mutates it.

```csharp
public class PrefabDoctorWindow : EditorWindow
{
    [SerializeField] private VisualTreeAsset _uxml; // optional

    public void CreateGUI()
    {
        var root = rootVisualElement;
        root.style.flexDirection = FlexDirection.Column;

        var toolbar = new Toolbar();
        root.Add(toolbar);

        var split = new TwoPaneSplitView(
            fixedPaneIndex: 0,
            fixedPaneStartDimension: 260f,
            orientation: TwoPaneSplitViewOrientation.Horizontal);
        root.Add(split);

        var leftPane = new VisualElement();
        var rightPane = new VisualElement();
        split.Add(leftPane);
        split.Add(rightPane);

        BuildToolbar(toolbar);
        BuildGameObjectList(leftPane);
        BuildConflictTable(rightPane);
    }
}
```

Impact: `OnGUI`'s try/catch safety net (Task 1) becomes unnecessary — UI Toolkit
does not maintain a Begin/End stack that a thrown exception can corrupt.

## `TwoPaneSplitView` — replace manual `_splitRatio` math

Current IMGUI split uses `position.width * _splitRatio` with a manually-tracked
`_isDraggingSplit` bool. `TwoPaneSplitView` does all of that, supports keyboard
resize, and persists the divider via `ViewDataKey`.

```csharp
var split = new TwoPaneSplitView(0, 260f, TwoPaneSplitViewOrientation.Horizontal)
{
    viewDataKey = "prefab-doctor-split"
};
```

Available since 2020.1 (experimental) → public in 2021.2+.

## `MultiColumnListView` — the critical virtualization win

Today, `DrawConflictList` renders **every** conflict row inside an
`EditorGUILayout.BeginScrollView`. With hierarchy analysis easily producing
2000+ rows (lightmap + network noise), this dominates frame time.
`MultiColumnListView` renders only the rows currently in view and recycles
bindings:

```csharp
var columns = new Columns
{
    new Column { name = "severity",  title = "Sev",      width = 40  },
    new Column { name = "category",  title = "Cat",      width = 60  },
    new Column { name = "component", title = "Component", width = 140 },
    new Column { name = "path",      title = "GameObject", width = 220 },
    new Column { name = "property",  title = "Property",   width = 220 },
    new Column { name = "value",     title = "Value",      width = Length.Percent(100) },
};

var table = new MultiColumnListView(columns)
{
    itemsSource = _filteredConflicts,
    selectionType = SelectionType.Multiple,
    showBorder = true,
    fixedItemHeight = 18,
    reorderable = false,
};

columns["severity"].makeCell  = () => new Label();
columns["severity"].bindCell  = (el, i) => ((Label)el).text =
    SeverityBadge(_filteredConflicts[i].Severity);

columns["category"].makeCell  = () => new Label();
columns["category"].bindCell  = (el, i) => ((Label)el).text =
    _filteredConflicts[i].Category.ToString();
// ...
rightPane.Add(table);
```

`MultiColumnListView` is available on Unity 6 with no caveats — this is the
single biggest reason the package minimum was set to `6000.0`.

## `ListView` — virtualized GameObject tree on the left

The left pane is just `_report.GameObjects`. `ListView` with
`fixedItemHeight` is the drop-in equivalent of our IMGUI loop over
`filteredGOs`, but virtualized and with built-in selection.

```csharp
var goList = new ListView
{
    itemsSource  = _filteredGameObjects,
    fixedItemHeight = 18,
    makeItem     = () => new Label(),
    bindItem     = (el, i) => ((Label)el).text = _filteredGameObjects[i].RelativePath,
    selectionType = SelectionType.Single,
};
goList.selectionChanged += objs => OnGameObjectSelected(goList.selectedIndex);
leftPane.Add(goList);
```

`ListView.selectionChanged` (plural) is the canonical event on Unity 6.

## Toolbar components

Replace the `EditorGUILayout.BeginHorizontal(EditorStyles.toolbar)` block:

```csharp
var toolbar = new Toolbar();
var analyzeBtn  = new ToolbarButton(() => RunAnalysis())          { text = "Analyze" };
var hierBtn     = new ToolbarButton(() => RunHierarchyAnalysis()) { text = "▼ Hierarchy" };

var filterMenu  = new ToolbarMenu { text = _filterMode.ToString() };
foreach (FilterMode mode in Enum.GetValues(typeof(FilterMode)))
    filterMenu.menu.AppendAction(mode.ToString(), _ => SetFilter(mode),
        _ => _filterMode == mode ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);

var defaultsToggle = new ToolbarToggle { text = "Defaults", value = _showDefaults };
defaultsToggle.RegisterValueChangedCallback(ev => _showDefaults = ev.newValue);

toolbar.Add(analyzeBtn);
toolbar.Add(hierBtn);
toolbar.Add(filterMenu);
toolbar.Add(new ToolbarSpacer { flex = true });
toolbar.Add(defaultsToggle);
```

Available on Unity 6 with no concerns.

## `ProgressBar` — replace `EditorUtility.DisplayProgressBar`

Our Task 2 blocking progress bar is synchronous and freezes the whole editor.
UI Toolkit's `ProgressBar` is a regular `VisualElement` we can show inside the
window:

```csharp
var bar = new ProgressBar { title = "Analyzing...", lowValue = 0, highValue = 1 };
bar.style.display = DisplayStyle.None;
root.Add(bar);

// When analysis starts incrementally:
bar.style.display = DisplayStyle.Flex;
bar.value = progress; // update each frame via schedule.Execute
```

This only works if analysis is incremental (we already have
`_incrementalJob` in the instance-analysis path). Hierarchy analysis would
need to be made incremental before this helps — that's a future refactor, not
part of this migration doc.

## USS theme variables

Unity provides `var(--unity-colors-*)` USS variables that track the editor
theme automatically. This replaces manual `EditorStyles.miniLabel` /
`EditorGUIUtility.isProSkin` branching.

```uss
.prefab-doctor-status-bar {
    background-color: var(--unity-colors-helpbox-background);
    border-top-width: 1px;
    border-top-color: var(--unity-colors-default-border);
    padding: 2px 6px;
}

.prefab-doctor-badge {
    -unity-font-style: bold;
    color: var(--unity-colors-label-text);
    margin-right: 6px;
}
.prefab-doctor-badge--pingpong { color: var(--unity-colors-error-text); }
```

Unity 6 ships the full set of `var(--unity-*)` theme tokens.

## `SerializedObject` binding

UI Toolkit can bind controls to a `SerializedObject` via `Bind(...)`. Our report
data is plain C# (`AnalysisReport`, `PropertyConflict`), so direct binding is
not available — binding requires the data to be either `SerializedProperty`
instances on a Unity Object or explicit `INotifyValueChanged` implementations.

**Recommendation:** don't try to bind the report. Keep the analyzer output as
plain C# and push it into `MultiColumnListView.itemsSource`. Bind only the
settings (`PrefabDoctorSettings` ScriptableObject) because it already lives on
a Unity Object.

```csharp
var settingsSO = new SerializedObject(PrefabDoctorSettings.instance);
root.Bind(settingsSO);
```

## Right-click context menus

IMGUI uses `GenericMenu` in mouse event code. UI Toolkit uses
`ContextualMenuManipulator`:

```csharp
conflictRow.AddManipulator(new ContextualMenuManipulator(ev =>
{
    ev.menu.AppendAction("Revert to base", _ => OverrideActions.Revert(conflict));
    ev.menu.AppendAction("Apply to source", _ => OverrideActions.Apply(conflict));
    ev.menu.AppendSeparator();
    ev.menu.AppendAction("Copy property path", _ =>
        EditorGUIUtility.systemCopyBuffer = conflict.Key.PropertyPath);
}));
```

Works with `MultiColumnListView` via the row `bindCell` callback (attach the
manipulator per-row).

## Version status

Package minimum is `"unity": "6000.0"`. Every API catalogued above
(`CreateGUI`, `TwoPaneSplitView`, `ListView`, `MultiColumnListView`, toolbar
components, `ProgressBar`, USS theme variables, `ContextualMenuManipulator`)
is first-class on Unity 6. No shims, no fallbacks, no conditional compilation
required. The port can proceed in a single pass whenever we decide to start
it.

## What this does NOT cover

- Drag-and-drop reordering of columns.
- Search box (`ToolbarSearchField`) — trivial add, not listed because it has
  no current IMGUI equivalent.
- Animation / transitions — not relevant to an analysis tool.
- The Project Scan tab — it has its own `ProjectScanPanel` with pending UX
  decisions; should be migrated in a separate pass.
