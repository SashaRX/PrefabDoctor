# Changelog

## [0.2.4] - 2026-04-16

### Fixed
- Hierarchy left-panel selection now survives filter changes by selected instance identity (InstanceID), preventing index-based row drift and mixed-looking right-panel context.

## [0.2.3] - 2026-04-16

### Fixed
- Hierarchy right panel now filters conflicts strictly by selected scene instance (by instance-scoped key / instance ID), avoiding accidental mix from other instances.
- Per-instance counters in the left list are now accumulated by instance ID from scoped keys, so counts stay bound to the correct selected instance.

## [0.2.2] - 2026-04-16

### Fixed
- Hierarchy mode left panel now lists concrete scene instances instead of only prefab-type groups, so selection and conflict browsing are anchored to the actual instance root.
- Hierarchy conflict table selection sync now works against the full visible row set (multi-GameObject view), improving batch selection behavior.

## [0.2.1] - 2026-04-15

### Fixed
- Hierarchy analysis UI/object resolution now uses an instance-scoped GameObject key (`instanceRootInstanceId:relativePath`) to avoid collisions when multiple prefab instances share identical relative paths.
- Batch actions and ping now resolve conflicts through the same instance-scoped key, preventing rows from being bound to the first matching instance.

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
