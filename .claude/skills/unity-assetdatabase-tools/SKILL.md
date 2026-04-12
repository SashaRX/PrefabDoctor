---
name: unity-assetdatabase-tools
description: Используй при написании AssetPostprocessor, батчинге AssetDatabase-операций с StartAssetEditing, управлении импортом или генерации HideAndDontSave-ассетов.
---

# AssetDatabase Tools

## Батчинг

```csharp
AssetDatabase.StartAssetEditing();
try
{
    foreach (var path in assetPaths) { /* операции */ }
}
finally
{
    AssetDatabase.StopAssetEditing();
    AssetDatabase.Refresh();
}
```

**ЗАПРЕЩЕНО:** `StartAssetEditing()` без `try/finally`.

## AssetPostprocessor

```csharp
class MyPostprocessor : AssetPostprocessor
{
    public override int GetPostprocessOrder() => 10000;
    static readonly HashSet<string> _bypassPaths = new();

    void OnPostprocessModel(GameObject go)
    {
        if (_bypassPaths.Contains(assetPath)) return;
        try
        {
            _bypassPaths.Add(assetPath);
            // обработка
        }
        finally { _bypassPaths.Remove(assetPath); }
    }
}
```

## GUID и пути

```csharp
string path = AssetDatabase.GUIDToAssetPath(guid);
var obj = AssetDatabase.LoadAssetAtPath<T>(path);
string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" });
```

## Генерируемые ассеты

```csharp
var tex = new Texture2D(16, 16);
tex.hideFlags = HideFlags.HideAndDontSave;
```

## Progress bar

```csharp
try
{
    for (int i = 0; i < count; i++)
    {
        if (EditorUtility.DisplayCancelableProgressBar("Title", $"{i}/{count}", (float)i/count))
            break;
    }
}
finally { EditorUtility.ClearProgressBar(); }
```

## Запреты

1. `StartAssetEditing` без try/finally
2. `Refresh()` внутри цикла
3. Рекурсивный импорт без bypass
4. `Resources.Load` в Editor
5. `FindAssets` без фильтра типа
