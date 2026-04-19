---
name: unity-serialized-workflow
description: Используй при работе с SerializedObject/SerializedProperty, создании CustomEditor/PropertyDrawer или сравнении сериализованных значений.
---

# Unity Serialized Workflow

Цикл: Update() → FindProperty → ApplyModifiedProperties(). ЗАПРЕЩЕНО: мутация target cast. BeginProperty/EndProperty в PropertyDrawer обязательны. Read-only доступ через new SerializedObject(obj).FindProperty(path).
