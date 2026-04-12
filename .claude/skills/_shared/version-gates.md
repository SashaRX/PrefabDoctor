# Version Gates — условная компиляция по версии Unity

## Когда использовать

Если API появилось в определённой версии Unity, а пакет поддерживает более старые версии — оборачивай в `#if` директиву с фолбэком.

## Формат

```csharp
#if UNITY_2022_2_OR_NEWER
    // Новый API
    PrefabUtility.RemoveUnusedOverrides(roots, InteractionMode.AutomatedAction);
#else
    // Ручной фолбэк для старых версий
    RemoveUnusedOverridesManual(prefab);
#endif
```

## Реальный пример

Из prefabdoctor/Editor/Core/ProjectScanActions.cs — `BatchRemoveUnusedOverrides()`:

```csharp
#if UNITY_2022_2_OR_NEWER
    var roots = new List<GameObject>();
    foreach (var path in prefabPaths)
    {
        var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
        if (prefab != null) roots.Add(prefab);
    }
    if (roots.Count > 0)
        PrefabUtility.RemoveUnusedOverrides(roots.ToArray(), InteractionMode.AutomatedAction);
#else
    AssetDatabase.StartAssetEditing();
    try
    {
        foreach (var path in prefabPaths)
            total += RemoveUnusedOverrides(path);
    }
    finally
    {
        AssetDatabase.StopAssetEditing();
        AssetDatabase.Refresh();
    }
#endif
```

## Через versionDefines в asmdef

Для опциональных зависимостей на другие пакеты (не версии Unity):

```json
"versionDefines": [{
  "name": "com.unity.formats.fbx",
  "expression": "[5.0.0,6.0.0)",
  "define": "LIGHTMAP_UV_TOOL_FBX_EXPORTER"
}]
```

Использование в коде:
```csharp
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
    // Код, требующий FBX Exporter пакет
#endif
```

## Распространённые маркеры версий

| Директива | Версия Unity | Примечание |
|-----------|-------------|----------|
| `UNITY_2021_3_OR_NEWER` | 2021.3 LTS | Минимум для новых пакетов |
| `UNITY_2022_2_OR_NEWER` | 2022.2 | `RemoveUnusedOverrides` batch API |
| `UNITY_2023_1_OR_NEWER` | 2023.1 | Новые prefab API |
| `UNITY_6000_0_OR_NEWER` | Unity 6 | Cutting edge |

## Правила

- Всегда предоставляй фолбэк в `#else` — не оставляй пустым
- `"unity"` в package.json = минимальная поддерживаемая версия, `#if` гейты = расширения для новых
- Тестируй на минимальной версии из package.json
