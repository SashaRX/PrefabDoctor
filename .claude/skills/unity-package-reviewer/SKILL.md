---
name: unity-package-reviewer
description: Используй при ревью диффа, PR или существующего файла на нарушения правил Unity-пакета. Указывает конкретный скилл, чьё правило нарушено.
---

# Unity Package Reviewer

Для каждого нарушения указывай файл, строку, скилл-источник и минимальный фикс.

## CRITICAL — блокируют мерж

| Что искать | Скилл |
|-----------|-------|
| `.mesh` вместо `.sharedMesh` | unity-undo-prefab-safety |
| `DestroyImmediate` без Undo | unity-undo-prefab-safety |
| Мутация prefab без EditPrefabContentsScope | unity-undo-prefab-safety |
| `StartAssetEditing` без try/finally | unity-assetdatabase-tools |
| Мутация Object без Undo.RecordObject | unity-undo-prefab-safety |
| Мутация target в CustomEditor | unity-serialized-workflow |
| Editor-код в Runtime asmdef | unity-package-architect |

## HIGH — требуют исправления

| Что искать | Скилл |
|-----------|-------|
| Ручной JSON-парсинг | unity-editor-tooling |
| UnityWebRequest без timeout | unity-editor-tooling |
| Забытый ApplyModifiedProperties | unity-serialized-workflow |
| AssetPostprocessor без bypass | unity-assetdatabase-tools |
| Файл >50 КБ | migration-and-refactor-planner |
| Захардкоженные пути/URL | unity-editor-tooling |

## LOW — рекомендации

| Что искать | Скилл |
|-----------|-------|
| Нет #if UNITY_VERSION | unity-undo-prefab-safety |
| Нет progress bar >10 ассетов | unity-assetdatabase-tools |
| Namespace без префикса | unity-package-architect |

## Правила

1. Проверяй КАЖДЫЙ .cs файл в диффе
2. .asmdef и package.json — сверяй с unity-package-architect
3. Только фиксы правил, не произвольный рефакторинг
