# Prefab Doctor

Make Prefab Great Again — Unity Editor tool for detecting and resolving override conflicts in deeply nested prefabs.

## Problem

Unity's nested prefab system stores overrides per-level, but provides no cross-level view. A property can be `true` → `false` → `true` across three nesting depths with no built-in way to detect this "ping-pong" pattern.

## Features

- **Ping-pong detection** — finds properties that flip back and forth across nesting levels
- **Multi-override view** — table showing every override at every depth simultaneously
- **Insignificant override cleanup** — removes overrides where value matches source within epsilon (float drift, quaternion equivalence, Euler wrap-around)
- **Orphan cleanup** — removes overrides targeting deleted components
- **Batch operations** — select multiple conflicts, revert/apply in one click
- **Full Undo support** — every operation is undoable

## Smart Comparisons

Prefab Doctor understands that not all differences are real differences:

| Type | Example | How it's handled |
|------|---------|-----------------|
| Float drift | `0.0000001` vs `0.0` | Absolute + relative epsilon |
| Scale noise | `1.0000001` vs `1.0` | Same epsilon logic |
| Quaternion sign | `(0,0,0,1)` vs `(0,0,-0,-1)` | Dot product comparison (q == -q) |
| Euler wrap | `0°` vs `360°`, `180°` vs `-180°` | Normalize to [0,360) first |
| Color channels | HDR float drift | Per-channel epsilon |

## Installation

### Git URL (recommended)
In Unity Package Manager → Add package from git URL:
```
https://github.com/SashaRX/PrefabDoctor.git
```

### Local
Clone the repo and add via Package Manager → Add package from disk → select `package.json`.

## Usage

1. **Tools → Prefab Doctor** (or `Ctrl+Alt+O`)
2. Select a prefab instance in the Hierarchy (or drag into the target field)
3. Click **Analyze**
4. Left panel shows GameObjects with colored badges (red = ping-pong, yellow = multi-override)
5. Right panel shows per-property override values at each nesting depth
6. Right-click any row for resolution options

## Requirements

- Unity 2021.3+
- Editor only (no runtime dependency)

## License

MIT
