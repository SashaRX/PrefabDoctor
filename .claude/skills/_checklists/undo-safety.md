# Чеклист: Undo-безопасность

Проверяй перед каждым коммитом, если код мутирует Unity Object.

- [ ] Каждая мутация Unity Object обёрнута в `Undo.RecordObject()` ДО мутации
- [ ] Группа операций: `Undo.SetCurrentGroupName() → GetCurrentGroup() → CollapseUndoOperations()`
- [ ] Или RAII: `using var scope = new UndoGroupScope(name);`
- [ ] `DestroyImmediate` только для temp-объектов в `finally`-блоке
- [ ] Для сценных объектов: `Undo.DestroyObjectImmediate()` вместо `Object.DestroyImmediate()`
- [ ] Нет `.mesh` — только `.sharedMesh` + клон перед мутацией
- [ ] CustomEditor: только через `SerializedObject.ApplyModifiedProperties()` (автоматический Undo)
- [ ] `SetPropertyModifications` предварён `Undo.RecordObject`

## Эталонные реализации

- **Undo-группы:** prefabdoctor/Editor/Core/OverrideActions.cs
- **UndoGroupScope RAII:** unitymeshlab/Editor/MeshHygieneUtility.cs
- **Mesh clone + Undo:** unitymeshlab/Editor/MeshHygieneUtility.cs — `PrepareWritable()`
