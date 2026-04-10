# Changelog

## [0.2.0] - 2026-04-10

### Added — Project Scanner
- Project-wide prefab health scan (Tools → Prefab Doctor → Project Scan tab)
- Detection: FBX-based prefabs without wrapper, missing scripts, broken references, unused overrides, bad materials (error/unsupported shaders)
- FBX Import Auditor: flags importMaterials/importAnimation/importCameras/importLights generating unnecessary overrides
- FBX→Wrapper index: finds existing prefab wrappers for each FBX model
- Actions: Create FBX Wrapper, Remove Missing Scripts, Remove Unused Overrides (uses built-in API on 2022.2+)
- Batch operations with AssetDatabase.StartAssetEditing/StopAssetEditing
- Incremental scanning with progress bar
- Context menu per result: ping, open prefab, copy path, ping FBX source
- Scan scope: entire project or specific folder

### Changed
- PrefabDoctorWindow now has two tabs: "Instance Analysis" (original) and "Project Scan" (new)

## [0.1.0] - 2026-04-10

### Added
- Initial release
- Prefab nesting chain walker
- Ping-pong override detection
- Multi-override conflict detection
- Orphaned override detection and cleanup
- Insignificant override detection and cleanup (float epsilon, quaternion equivalence, Euler normalization)
- Two-panel EditorWindow: GameObject tree + override conflict table
- Context menu with Revert All / Keep Only At Depth / batch operations
- Full Undo support for all actions
- Filter modes: Conflicts Only, All Overrides, Ping-Pong Only, Orphans Only, Insignificant Only
