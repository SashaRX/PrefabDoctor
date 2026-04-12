# Антипаттерны Unity Editor-пакетов

Консолидированный справочник запретов из всех скиллов. Каждый пункт ссылается на скилл с правильным паттерном.

## CRITICAL — никогда не делай

### 1. `.mesh` вместо `.sharedMesh` в Editor
**Скилл:** unity-undo-prefab-safety
**Проблема:** `MeshFilter.mesh` создаёт скрытый инстанс, утечка памяти.
**Правильно:** `meshFilter.sharedMesh` + клон перед мутацией.

### 2. Мутация prefab-ассета через LoadAssetAtPath → мутация → SaveAssets
**Скилл:** unity-undo-prefab-safety
**Проблема:** Обходит prefab pipeline, ломает вложенные prefabs.
**Правильно:** `PrefabUtility.EditPrefabContentsScope` или temp-instance паттерн.

### 3. `StartAssetEditing()` без `try/finally`
**Скилл:** unity-assetdatabase-tools
**Проблема:** При исключении Unity зависнет — `StopAssetEditing` не вызовется.
**Правильно:** `try { ... } finally { StopAssetEditing(); Refresh(); }`

### 4. Мутация Unity Object без Undo
**Скилл:** unity-undo-prefab-safety
**Проблема:** Пользователь не сможет отменить действие (Ctrl+Z).
**Правильно:** `Undo.RecordObject(target, "описание")` ПЕРЕД мутацией.

### 5. Прямая мутация target в CustomEditor
**Скилл:** unity-serialized-workflow
**Проблема:** Undo не записывается, prefab overrides ломаются.
**Правильно:** `serializedObject.Update() → FindProperty → ApplyModifiedProperties()`

### 6. Editor-код в Runtime asmdef
**Скилл:** unity-package-architect
**Проблема:** Не компилируется в билде.
**Правильно:** `includePlatforms: ["Editor"]` в asmdef.

### 7. `DestroyImmediate` на сценных объектах без Undo
**Скилл:** unity-undo-prefab-safety
**Проблема:** Необратимое удаление.
**Правильно:** `Undo.DestroyObjectImmediate()` для сценных, `Object.DestroyImmediate()` только для temp-объектов в `finally`.

## HIGH — избегай

### 8. Ручной JSON-парсинг
**Скилл:** unity-editor-tooling
**Реальный пример:** asset-lock-board/Editor/AssetLockBoard.cs (~стр.279-320) — ручной обход JSON скобок через IndexOf.
**Правильно:** `JsonUtility.FromJson<T>()` или Newtonsoft.

### 9. `UnityWebRequest` без timeout
**Скилл:** unity-editor-tooling
**Правильно:** `request.timeout = 10;` перед `SendWebRequest()`.

### 10. `AssetPostprocessor` без bypass-защиты
**Скилл:** unity-assetdatabase-tools
**Проблема:** Рекурсивный импорт (бесконечный цикл).
**Правильно:** `static HashSet<string> _bypassPaths` + проверка перед обработкой.

### 11. `Resources.Load` в Editor-коде
**Скилл:** unity-assetdatabase-tools
**Правильно:** `AssetDatabase.LoadAssetAtPath<T>(path)`.

### 12. `AssetDatabase.Refresh()` внутри цикла
**Скилл:** unity-assetdatabase-tools
**Правильно:** Только после завершения батча.

### 13. Захардкоженные пути и URL
**Скилл:** unity-editor-tooling
**Реальный пример:** asset-lock-board — Firebase URL в const. unitymeshlab — repository.url drift.
**Правильно:** Параметры, EditorPrefs, конфигурационные ScriptableObject.

### 14. Забытый `ApplyModifiedProperties()`
**Скилл:** unity-serialized-workflow
**Проблема:** Правки через SerializedProperty не сохраняются.

### 15. Забытый `ClearProgressBar()`
**Скилл:** unity-assetdatabase-tools
**Правильно:** Всегда в `finally` блоке.
