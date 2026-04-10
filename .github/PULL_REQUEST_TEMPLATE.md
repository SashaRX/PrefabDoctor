## Summary
<!-- 1-3 bullet points: what changed and why -->

-

## Changed Zones
<!-- Check all that apply -->

- [ ] `Editor/Core/` — Analysis engine, actions
- [ ] `Editor/Comparers/` — Epsilon comparison logic
- [ ] `Editor/UI/` — EditorWindow, menus
- [ ] `Editor/Settings/` — Settings ScriptableObject
- [ ] `package.json` — Version / dependencies
- [ ] `.github/` — CI / workflows
- [ ] Docs (`README.md`, `CHANGELOG.md`)

## Checklist

- [ ] `.meta` files present for all new files/directories
- [ ] No Editor ↔ Runtime dependency leaks
- [ ] Undo support for all prefab modifications
- [ ] `SetPropertyModifications` uses read-modify-write (not partial write)
- [ ] `PropertyModification.target` null-checked
- [ ] SerializedObject cache cleared after analysis
- [ ] CHANGELOG.md updated (if user-visible change)

## Test Plan
<!-- How to verify: which prefab structure, expected behavior -->

-

## Review Notes
<!-- Risks, trade-offs, open questions -->

