---
name: unity-undo-prefab-safety
description: Используй при мутации prefab-ассетов, записи Undo-групп, редактировании prefab-оверрайдов или клонировании asset-backed мешей.
---

# Undo и Prefab Safety

Undo-группы: SetCurrentGroupName → GetCurrentGroup → CollapseUndoOperations. RAII: UndoGroupScope. Prefab: EditPrefabContentsScope. Temp-instance: try/finally DestroyImmediate. Оверрайды: Get/SetPropertyModifications. Mesh: .sharedMesh + клон. ЗАПРЕЩЕНО: .mesh, LoadAssetAtPath→мутация→SaveAssets, DestroyImmediate без Undo.
