---
name: unity-serialized-workflow
description: Используй при работе с SerializedObject/SerializedProperty, создании CustomEditor/PropertyDrawer или сравнении сериализованных значений.
---

# Unity Serialized Workflow

## Основной цикл (CustomEditor)

```csharp
[CustomEditor(typeof(MyComponent))]
public class MyComponentEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("myField"));
        serializedObject.ApplyModifiedProperties();
    }
}
```

**ЗАПРЕЩЕНО:** `((MyComponent)target).myField = value` — ломает Undo и prefab overrides.

## Read-only доступ (паттерн PrefabDoctor)

Источник: prefabdoctor/Editor/Core/OverrideActions.cs — `GetSourcePropertyValue()`

```csharp
static string GetPropertyValue(Object obj, string propertyPath)
{
    var so = new SerializedObject(obj);
    var prop = so.FindProperty(propertyPath);
    if (prop == null) return null;
    return prop.propertyType switch
    {
        SerializedPropertyType.Float   => prop.floatValue.ToString(CultureInfo.InvariantCulture),
        SerializedPropertyType.Integer => prop.intValue.ToString(),
        SerializedPropertyType.Boolean => prop.boolValue ? "1" : "0",
        SerializedPropertyType.String  => prop.stringValue,
        SerializedPropertyType.Enum    => prop.enumValueIndex.ToString(),
        SerializedPropertyType.Color   => prop.colorValue.ToString(),
        SerializedPropertyType.Vector3 => prop.vector3Value.ToString(),
        _ => null
    };
}
```

## CustomPropertyDrawer

```csharp
[CustomPropertyDrawer(typeof(MyAttribute))]
public class MyDrawer : PropertyDrawer
{
    public override void OnGUI(Rect pos, SerializedProperty prop, GUIContent label)
    {
        EditorGUI.BeginProperty(pos, label, prop);
        // рисуй
        EditorGUI.EndProperty();
    }
}
```

`BeginProperty/EndProperty` — обязательно для prefab override UI.

## Итерация

```csharp
var it = so.GetIterator();
if (it.NextVisible(true))
    do { /* it.propertyPath */ } while (it.NextVisible(false));
```

## Вложенные свойства

```csharp
var list = so.FindProperty("myList");
for (int i = 0; i < list.arraySize; i++)
{
    var el = list.GetArrayElementAtIndex(i);
    var nested = el.FindPropertyRelative("nestedField");
}
```

## Запреты

1. Мутация через `target` cast без Undo
2. Забытый `ApplyModifiedProperties()`
3. Забытый `Update()` перед чтением
4. `FindProperty` с опечаткой — проверяй null
