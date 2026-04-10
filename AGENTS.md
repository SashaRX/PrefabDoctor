# AI Agent Rules — Unity Package (com.sasharx.override-doctor)

Shared rules for **all AI agents** (Codex, Claude, etc.) working on this repository.

## Package Structure & Zones

| Zone | Purpose | Sensitivity |
|------|---------|-------------|
| `Editor/Core/` | Analysis engine, actions, data models | High — correctness critical |
| `Editor/Comparers/` | Epsilon comparison logic | High — false positives/negatives |
| `Editor/UI/` | EditorWindow, menu items | Medium — UX |
| `Editor/Settings/` | ScriptableObject settings | Low — user config |
| `package.json` | UPM manifest | High — version, dependencies |
| `CHANGELOG.md` | Release notes | Low — documentation |

## Hard Rules

### Meta files
- Every file and directory MUST have a `.meta` file
- NEVER delete, regenerate, or modify GUIDs in `.meta` files
- Do NOT create `.meta` manually — Unity generates them
- Do NOT commit bulk `.meta` changes unless files were actually added/removed

### Assembly & platform
- All code under `Editor/` with `SashaRX.OverrideDoctor.Editor.asmdef`
- `includePlatforms: ["Editor"]` — never leak into runtime builds
- No runtime dependencies — this is a pure editor tool

### Package integrity
- Do NOT change `package.json` name/displayName without explicit request
- Do NOT modify public API without changelog entry
- CHANGELOG.md updated for every user-visible change

### PrefabUtility safety
- `GetPropertyModifications` must receive outermost prefab instance root
- `SetPropertyModifications` replaces ALL modifications — always read full array, filter, write back
- `GetCorrespondingObjectFromSource` = one level up (use this, not `FromOriginalSource`)
- Always null-check `PropertyModification.target` — orphaned mods are common
- Always filter `IsDefaultOverride` unless explicitly including defaults
- Guard against infinite loops in chain walk (visited set by InstanceID)

### Undo
- All prefab modifications via `Undo.RecordObject` before `SetPropertyModifications`
- Group related operations with `Undo.SetCurrentGroupName` + `CollapseUndoOperations`

### Code conventions
- Namespace: `SashaRX.OverrideDoctor`
- `internal` visibility for cross-tool helpers
- Float parsing: always `CultureInfo.InvariantCulture`
- Property comparison: route through `ComparerRouter`, never raw string compare for numerics

## Review Focus (Critical Issues)

1. **PropertyModification corruption** — partial writes via `SetPropertyModifications` losing unrelated overrides
2. **Chain walk bugs** — wrong depth mapping, infinite loops, missing levels
3. **Quaternion comparison** — forgetting q == -q equivalence, per-component instead of grouped
4. **Epsilon false positives** — cleaning overrides that are actually meaningful
5. **Undo support** — all prefab modifications must be undoable
6. **SerializedObject leaks** — not disposing/clearing cached SOs after analysis
7. **Performance** — O(n²) in ping-pong detection on large override sets, SO creation in hot paths
8. **asmdef** — must stay Editor-only, no runtime references
