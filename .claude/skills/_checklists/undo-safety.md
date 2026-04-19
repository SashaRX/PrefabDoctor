# Чеклист: Undo-безопасность

- [ ] `Undo.RecordObject()` ДО мутации
- [ ] `SetCurrentGroupName → GetCurrentGroup → CollapseUndoOperations`
- [ ] `DestroyImmediate` только в `finally` для temp-объектов
- [ ] Только `.sharedMesh` + клон
- [ ] `ApplyModifiedProperties()` в CustomEditor
- [ ] `Undo.RecordObject` перед `SetPropertyModifications`
