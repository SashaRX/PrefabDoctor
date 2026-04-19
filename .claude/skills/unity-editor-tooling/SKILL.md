---
name: unity-editor-tooling
description: Используй при создании EditorWindow, MenuItem, [InitializeOnLoad], [OnOpenAsset], AssetModificationProcessor или других точек входа Unity Editor.
---

# Unity Editor Tooling

Точки входа: EditorWindow (GetWindow/CreateInstance), AssetModificationProcessor (OnWillSaveAssets, IsOpenForEdit), [OnOpenAsset], [InitializeOnLoad]. Запреты: ручной JSON, UnityWebRequest без timeout, статическое состояние без контракта, тяжёлые операции в OnGUI, Editor API в Runtime asmdef.
