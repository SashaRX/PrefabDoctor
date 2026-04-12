---
name: unity-editor-tooling
description: Используй при создании EditorWindow, MenuItem, [InitializeOnLoad], [OnOpenAsset], AssetModificationProcessor или других точек входа Unity Editor.
---

# Unity Editor Tooling

Этот скилл покрывает «оболочку» Editor-тулов: точки входа, UI, подписки на события, IO. Мутация ассетов — в скиллах unity-assetdatabase-tools и unity-undo-prefab-safety.

## Точки входа

### EditorWindow

```csharp
public class MyToolWindow : EditorWindow
{
    [MenuItem("Tools/My Package/My Tool")]
    static void Open() => GetWindow<MyToolWindow>("My Tool");
}
```

- `GetWindow<T>()` — синглтон-окно (одно на Editor)
- `CreateInstance<T>()` — если нужно несколько инстансов
- Меню: `Tools/PackageName/ToolName` — стандартный путь для тулов

### AssetModificationProcessor

Перехват сохранения и открытия ассетов (паттерн из asset-lock-board/AssetLockSaveGuard.cs):

```csharp
class MySaveGuard : AssetModificationProcessor
{
    static string[] OnWillSaveAssets(string[] paths)
    {
        var allowed = new List<string>(paths.Length);
        foreach (var path in paths)
        {
            if (ShouldBlock(path))
            {
                Debug.LogWarning($"[MyTool] Blocked save: {path}");
                continue;
            }
            allowed.Add(path);
        }
        return allowed.ToArray();
    }

    static bool IsOpenForEdit(string path, out string message)
    {
        message = "";
        if (IsLocked(path))
        {
            message = "Locked by another user";
            return false;
        }
        return true;
    }
}
```

### [OnOpenAsset]

```csharp
[OnOpenAsset(0)]
static bool OnOpen(int instanceId, int line)
{
    return false; // true = перехватили
}
```

### [InitializeOnLoad]

```csharp
[InitializeOnLoad]
static class MyEditorHook
{
    static MyEditorHook()
    {
        EditorApplication.projectWindowItemOnGUI += OnProjectWindowItem;
        // НЕ выполняй тяжёлую работу здесь
    }
}
```

## Запреты

### 1. Ручной JSON-парсинг — ЗАПРЕЩЕНО

```csharp
// ПЛОХО:
int start = json.IndexOf('{');

// ХОРОШО:
var data = JsonUtility.FromJson<MyDataClass>(json);
```

### 2. UnityWebRequest без timeout — ЗАПРЕЩЕНО

```csharp
var request = UnityWebRequest.Get(url);
request.timeout = 10;
request.SendWebRequest();
```

### 3. Статическое мутабельное состояние без контракта — ЗАПРЕЩЕНО

Документируй lifetime: когда инициализируется и очищается.

### 4. Тяжёлые операции в OnGUI — ЗАПРЕЩЕНО

Кэшируй результаты, не вызывай `AssetDatabase.FindAssets` каждый кадр.

### 5. Editor API в Runtime asmdef — ЗАПРЕЩЕНО

`EditorGUIUtility`, `SceneView`, `AssetDatabase` — только в Editor asmdef.

## IO из Editor

- **UnityWebRequest**: обязателен `timeout`, обязателен `using` или `Dispose()`
- **EditorPrefs**: для пользовательских настроек (per-machine)
- **ProjectSettings/*.json**: для проектных настроек (коммитятся в VCS)
- **HideFlags.HideAndDontSave**: для сгенерированных Editor-only ассетов
