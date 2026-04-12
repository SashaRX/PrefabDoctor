# Чеклист: безопасность батч-операций

Проверяй если код обрабатывает множество ассетов.

- [ ] `AssetDatabase.StartAssetEditing()` обёрнут в `try/finally { StopAssetEditing(); Refresh(); }`
- [ ] Progress bar для >10 ассетов: `EditorUtility.DisplayCancelableProgressBar()`
- [ ] `ClearProgressBar()` в `finally`-блоке
- [ ] Нет `AssetDatabase.Refresh()` внутри цикла — только после батча
- [ ] `AssetPostprocessor` имеет bypass-множество (`HashSet<string>`) против рекурсии
- [ ] `GetPostprocessOrder()` указан явно (не дефолтный 0)
- [ ] Нет `AssetDatabase.ImportAsset()` внутри постпроцессора без bypass-защиты

## Эталонные реализации

- **Батчинг с try/finally:** prefabdoctor/Editor/Core/ProjectScanActions.cs — `BatchCreateWrappers()`, `BatchRemoveMissingScripts()`
- **AssetPostprocessor с bypass:** unitymeshlab/Editor/Uv2AssetPostprocessor.cs
