---
name: unity-undo-prefab-safety
description: Используй при мутации prefab-ассетов, записи Undo-групп, редактировании prefab-оверрайдов или клонировании asset-backed мешей перед модификацией.
---

# Undo и Prefab Safety

## Undo-группы

Каждая пользовательская операция = одна запись в Undo:

```csharp
Undo.SetCurrentGroupName("My Tool: операция");
int group = Undo.GetCurrentGroup();
try
{
    Undo.RecordObject(target, "описание");
    // мутации...
}
finally
{
    Undo.CollapseUndoOperations(group);
}
```

### RAII-вариант (unitymeshlab/Editor/MeshHygieneUtility.cs)

```csharp
readonly struct UndoGroupScope : IDisposable
{
    readonly int _group;
    public UndoGroupScope(string name)
    {
        Undo.SetCurrentGroupName(name);
        _group = Undo.GetCurrentGroup();
    }
    public void Dispose() => Undo.CollapseUndoOperations(_group);
}
```

## Редактирование prefab-ассетов

### EditPrefabContentsScope (рекомендуемый)

```csharp
using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
{
    var root = scope.prefabContentsRoot;
    // мутируй root — автосохранение при выходе
}
```

### Temp-instance паттерн

```csharp
var instance = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
try
{
    PrefabUtility.SaveAsPrefabAsset(instance, wrapperPath);
}
finally
{
    Object.DestroyImmediate(instance);
}
```

### Правка оверрайдов

```csharp
Undo.RecordObject(prefabRoot, "Remove override");
var mods = PrefabUtility.GetPropertyModifications(prefabRoot);
var filtered = mods.Where(m => /* условие */).ToArray();
PrefabUtility.SetPropertyModifications(prefabRoot, filtered);
```

## Mesh-безопасность

```csharp
// ЗАПРЕЩЕНО: meshFilter.mesh (утечка)
// ПРАВИЛЬНО:
Undo.RecordObject(meshFilter, "Clone mesh");
var clone = Object.Instantiate(meshFilter.sharedMesh);
clone.name = meshFilter.sharedMesh.name;
Undo.RecordObject(clone, "Modify mesh");
meshFilter.sharedMesh = clone;
```

## Version-гейты

```csharp
#if UNITY_2022_2_OR_NEWER
    PrefabUtility.RemoveUnusedOverrides(roots, InteractionMode.AutomatedAction);
#else
    RemoveUnusedOverridesManual(prefab);
#endif
```

## Абсолютные запреты

1. `LoadAssetAtPath<GameObject>` → мутация → `SaveAssets` — используй EditPrefabContentsScope
2. `DestroyImmediate` на сценных без Undo — используй `Undo.DestroyObjectImmediate()`
3. `.mesh` вместо `.sharedMesh` в Editor
4. Прямая мутация target для prefab-оверрайдов
5. Мутация без `Undo.RecordObject`
