# Чеклист: Prefab-безопасность

Проверяй если код редактирует prefab-ассеты или оверрайды.

- [ ] Редактирование prefab-ассета через `PrefabUtility.EditPrefabContentsScope`
- [ ] Или через `LoadPrefabContents` / `UnloadPrefabContents` (с обязательным UnloadPrefabContents в finally)
- [ ] НЕ через `LoadAssetAtPath<GameObject>` → мутация → `SaveAssets`
- [ ] Temp-instance паттерн: `InstantiatePrefab → try { SaveAsPrefabAsset } finally { DestroyImmediate(instance) }`
- [ ] Оверрайды: `GetPropertyModifications/SetPropertyModifications`, НЕ прямая мутация target-полей
- [ ] `Undo.RecordObject` ПЕРЕД `SetPropertyModifications`
- [ ] `#if UNITY_2022_2_OR_NEWER` для `RemoveUnusedOverrides` и подобных новых API
- [ ] Фолбэк в `#else` для старых версий Unity

## Эталонные реализации

- **EditPrefabContentsScope:** prefabdoctor/Editor/Core/ProjectScanActions.cs — `RemoveMissingScripts()`
- **Temp-instance:** prefabdoctor/Editor/Core/ProjectScanActions.cs — `CreateFbxWrapper()`
- **PropertyModifications:** prefabdoctor/Editor/Core/OverrideActions.cs — `CleanOrphans()`, `RemoveModification()`
- **Version gate:** prefabdoctor/Editor/Core/ProjectScanActions.cs — `BatchRemoveUnusedOverrides()`
