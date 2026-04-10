# Review Rules — Claude Review Mode

When Claude is invoked for PR review (not implementation), follow these rules.

## When to Request Claude Review

- Changes to `OverrideAnalyzer.cs` (chain walk, ping-pong detection, classification)
- Changes to `OverrideActions.cs` (SetPropertyModifications usage, Undo)
- Changes to `PropertyComparers.cs` (epsilon thresholds, quaternion logic)
- Public API changes
- Package version or asmdef changes

## Review Checklist

### Package Integrity
- [ ] `package.json` version follows semver
- [ ] `asmdef` Editor-only, no runtime references
- [ ] No Runtime ↔ Editor dependency leaks

### PrefabUtility Correctness
- [ ] `GetPropertyModifications` called on outermost root only
- [ ] `SetPropertyModifications` does read-modify-write (never partial)
- [ ] `GetCorrespondingObjectFromSource` used (not `FromOriginalSource`) for chain walk
- [ ] `PropertyModification.target` null-checked before access
- [ ] `IsDefaultOverride` filtered unless explicitly including
- [ ] Chain walk has infinite-loop guard

### Comparison Correctness
- [ ] Float comparison uses epsilon (never raw string equals for numerics)
- [ ] Quaternion comparison via Dot product with sign normalization
- [ ] Euler angles normalized to [0,360) before comparison
- [ ] No false positives in insignificant detection (could delete real overrides)

### Editor Safety
- [ ] All modifications use Undo.RecordObject before SetPropertyModifications
- [ ] Undo groups collapsed for batch operations
- [ ] SerializedObject cache cleared after analysis run
- [ ] No allocations in per-frame incremental pump (PumpIncrementalJob)

### Backward Compatibility
- [ ] Existing `.meta` GUIDs preserved
- [ ] Settings ScriptableObject fields not renamed (breaks saved assets)

## Review Output Format

For each finding:
- **Severity**: P0 (blocker), P1 (should fix), P2 (nice to have)
- **File:Line**: exact location
- **Issue**: one sentence
- **Suggestion**: concrete fix

Skip praise and obvious observations. Focus on things that could break.
