# Naming Conventions — конвенции именования

## Namespace

**Формат:** `Company.PackageName`

| Хорошо | Плохо |
|--------|-------|
| `SashaRX.PrefabDoctor` | `PrefabDoctor` (нет авторского префикса) |
| `SashaRX.MeshLab` | `LightmapUvTool` (имя инструмента, не пакета) |
| `SashaRX.AssetLockBoard` | `AssetLockBoard.Editor` (Editor в namespace — лишнее) |

Авторский префикс обязателен — предотвращает коллизии с другими пакетами.

## package.json name

**Формат:** `com.company.package-name` (kebab-case, lowercase)

| Хорошо | Плохо |
|--------|-------|
| `com.sasharx.prefab-doctor` | `com.sasharx.PrefabDoctor` (PascalCase) |
| `com.sasharx.mesh-lab` | `com.sasharx.lightmap-uv-tool` (имя фичи, не пакета) |

## asmdef name

**Формат:** `Company.PackageName.Assembly`

| Тип | Формат | Пример |
|-----|--------|--------|
| Editor | `Company.PackageName.Editor` | `SashaRX.PrefabDoctor.Editor` |
| Runtime | `Company.PackageName` | `SashaRX.PrefabDoctor` |
| Tests Editor | `Company.PackageName.Tests.Editor` | `SashaRX.PrefabDoctor.Tests.Editor` |
| Tests Runtime | `Company.PackageName.Tests` | `SashaRX.PrefabDoctor.Tests` |

## Директории

| Директория | Назначение | Конвенция |
|-----------|-----------|----------|
| `Editor/` | Editor-only код | PascalCase поддиректории: `Core/`, `UI/`, `Settings/` |
| `Runtime/` | Runtime-код | PascalCase |
| `Tests/Editor/` | EditMode-тесты | Зеркалит структуру Editor/ |
| `Documentation~/` | Документация | Тильда скрывает от Unity |
| `Samples~/` | Примеры | Тильда скрывает |
| `Native~/` | Исходники нативных DLL | Тильда скрывает |

## Файлы

| Тип | Конвенция | Пример |
|-----|-----------|--------|
| Класс | PascalCase, один класс = один файл | `OverrideAnalyzer.cs` |
| EditorWindow | `*Window.cs` | `PrefabDoctorWindow.cs` |
| CustomEditor | `*Editor.cs` или `*Inspector.cs` | `MyComponentEditor.cs` |
| PropertyDrawer | `*Drawer.cs` | `MyAttributeDrawer.cs` |
| AssetPostprocessor | `*Postprocessor.cs` | `Uv2AssetPostprocessor.cs` |
| AssetModificationProcessor | `*SaveGuard.cs` или `*ModProcessor.cs` | `AssetLockSaveGuard.cs` |
| Тесты | `*Tests.cs` | `OverrideAnalyzerTests.cs` |
| MenuItem-хелпер | `*MenuItems.cs` | `PrefabDoctorMenuItems.cs` |

## MenuItem пути

**Формат:** `Tools/Package Name/Action` или `GameObject/Package Name/Action`

```csharp
[MenuItem("Tools/Mesh Lab/UV Transfer")]         // Тулы в Tools/
[MenuItem("GameObject/Prefab Doctor/Analyze")]    // Контекст в GameObject/
```

## Константы и магические строки

- EditorPrefs ключи: `"PACKAGE_ABBREVIATION_KeyName"` (напр. `"ALB_UserId"`)
- Не хардкодить пути `"Assets/..."` — вычислять динамически
- Не хардкодить URL — выносить в const или конфиг
