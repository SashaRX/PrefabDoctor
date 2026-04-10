# Claude — Executor Mode

Claude's role in this repo: **write code, fix bugs, implement features, fix CI**.
Claude does NOT do final review — that's Codex's job.

See `AGENTS.md` for shared rules that apply to all AI agents.

## Workflow

1. Before changes — short plan (what, where, why)
2. Small, focused changes — one concern per commit
3. Do NOT touch unrelated files
4. Do NOT bulk-rename/reformat unless explicitly asked
5. Verify compile locally before proposing PR
6. Split large tasks into small PRs

## Code Rules

- Namespace: `SashaRX.PrefabDoctor`
- `internal` visibility for cross-tool helpers (same assembly)
- `Undo.RecordObject` / `Undo.CollapseUndoOperations` for all prefab modifications
- Logging via `Debug.Log("[Prefab Doctor] ...")` prefix
- All PropertyModification writes use read-modify-write pattern on full array
- SerializedObject instances cached per analysis run, cleared after
- Never create .meta files manually — Unity generates them

## Architecture Quick Reference

- **Entry point:** `Editor/UI/PrefabDoctorWindow.cs` — main EditorWindow (Tools → Prefab Doctor)
- **Context menu:** `Editor/UI/PrefabDoctorMenuItems.cs` — Hierarchy right-click integration
- **Core engine:** `Editor/Core/OverrideAnalyzer.cs` — chain walk, override collection, ping-pong detection
- **Actions:** `Editor/Core/OverrideActions.cs` — revert/apply/clean with Undo
- **Models:** `Editor/Core/OverrideModels.cs` — data structures
- **Comparers:** `Editor/Comparers/PropertyComparers.cs` — epsilon float, quaternion Dot, Euler normalization
- **Settings:** `Editor/Settings/PrefabDoctorSettings.cs` — configurable epsilons via ScriptableObject

## Key Patterns

- Chain walk: `GetCorrespondingObjectFromSource` one level at a time (never `FromOriginalSource`)
- Canonical key: `ComponentType:GameObjectPath::PropertyPath` — stable across nesting depths
- Quaternion grouping: collect .x/.y/.z/.w → reconstruct Quaternion → normalize sign → Dot compare
- PropertyModification target: always points one level up, not to base asset
- `SetPropertyModifications` replaces ALL mods — always read-modify-write full array
- Incremental analysis: `IEnumerator<float>` pumped via `EditorApplication.update` with 16ms frame budget
