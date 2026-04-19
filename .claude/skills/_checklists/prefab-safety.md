# Чеклист: Prefab-безопасность

- [ ] `EditPrefabContentsScope` или `LoadPrefabContents/UnloadPrefabContents`
- [ ] НЕ `LoadAssetAtPath<GameObject>` → мутация → `SaveAssets`
- [ ] Temp-instance: try/finally DestroyImmediate
- [ ] Оверрайды: Get/SetPropertyModifications
- [ ] `#if UNITY_2022_2_OR_NEWER` + фолбэк
