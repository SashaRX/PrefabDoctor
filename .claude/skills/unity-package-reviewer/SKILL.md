---
name: unity-package-reviewer
description: Используй при ревью диффа, PR или существующего файла на нарушения правил Unity-пакета.
---

# Unity Package Reviewer

CRITICAL: .mesh, DestroyImmediate без Undo, prefab без scope, StartAssetEditing без try/finally, мутация без Undo, target cast, Editor в Runtime. HIGH: ручной JSON, нет timeout, ApplyModifiedProperties, bypass, >50КБ, хардкод. LOW: #if, progress bar, namespace.
