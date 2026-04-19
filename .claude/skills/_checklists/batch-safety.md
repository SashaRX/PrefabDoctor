# Чеклист: безопасность батч-операций

- [ ] `StartAssetEditing()` в `try/finally { StopAssetEditing(); Refresh(); }`
- [ ] Progress bar для >10 ассетов
- [ ] `ClearProgressBar()` в `finally`
- [ ] Нет `Refresh()` внутри цикла
- [ ] AssetPostprocessor с bypass HashSet
- [ ] `GetPostprocessOrder()` явно
