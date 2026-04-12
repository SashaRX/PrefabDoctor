---
name: unity-package-architect
description: Используй при проектировании или исправлении package.json, asmdef, структуры Editor/Runtime/Tests~/Samples~/Documentation~/Native~, или ограничений версии Unity.
---

# Unity Package Architect

## Эталонная структура пакета

```
com.company.package-name/
├── package.json
├── README.md
├── CHANGELOG.md
├── LICENSE
├── Editor/
│   ├── Company.PackageName.Editor.asmdef
│   └── *.cs
├── Runtime/                          # только если есть runtime-код
│   ├── Company.PackageName.asmdef
│   └── *.cs
├── Tests/
│   └── Editor/
│       ├── Company.PackageName.Tests.Editor.asmdef
│       └── *Tests.cs
├── Documentation~/
│   └── index.md
├── Samples~/
│   └── BasicUsage/
└── Native~/                          # исходники нативных плагинов (скрыто от Unity)
```

## Правила package.json

```json
{
  "name": "com.company.package-name",
  "version": "1.0.0",
  "displayName": "Human Readable Name",
  "description": "Одно предложение с описанием назначения.",
  "unity": "2021.3",
  "author": { "name": "Author", "url": "https://github.com/author" },
  "repository": { "type": "git", "url": "https://github.com/author/repo.git" },
  "license": "MIT",
  "dependencies": {}
}
```

### Обязательно

- `name`: `com.company.package-name` (kebab-case, lowercase)
- `version`: semver (MAJOR.MINOR.PATCH)
- `unity`: минимальная поддерживаемая LTS (напр. `"2021.3"`), НЕ bleeding edge (`"6000.0"` только если нужны API Unity 6)
- `repository.url`: ОБЯЗАН совпадать с реальным URL репозитория
- `dependencies`: явный объект, даже если пустой `{}`

### Запрещено

- Нестандартные поля: `"type"`, `"main"`, `"module"` — это npm, не UPM
- Зависимости на конкретный patch (`@5.1.5` вместо `@5.1.0`) без причины

### Для монорепо

Если Unity-пакет в подпапке (как `asset-lock-board/unity/AssetLockBoard/`):
- Git URL установки: `https://github.com/author/repo.git?path=unity/AssetLockBoard`
- Документировать в README

## Правила asmdef

### Editor-only

```json
{
  "name": "Company.PackageName.Editor",
  "rootNamespace": "Company.PackageName",
  "references": [],
  "includePlatforms": ["Editor"],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "autoReferenced": true
}
```

### Runtime

```json
{
  "name": "Company.PackageName",
  "rootNamespace": "Company.PackageName",
  "references": [],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "autoReferenced": true
}
```

### Tests

```json
{
  "name": "Company.PackageName.Tests.Editor",
  "rootNamespace": "Company.PackageName.Tests",
  "references": ["Company.PackageName.Editor"],
  "includePlatforms": ["Editor"],
  "defineConstraints": ["UNITY_INCLUDE_TESTS"],
  "optionalUnityReferences": ["TestAssemblies"],
  "autoReferenced": false
}
```

### Правила

- `allowUnsafeCode: true` только при наличии `unsafe` кода
- `versionDefines` для опциональных зависимостей:
  ```json
  "versionDefines": [{
    "name": "com.unity.formats.fbx",
    "expression": "[5.0.0,6.0.0)",
    "define": "HAS_FBX_EXPORTER"
  }]
  ```
- Ссылки — только необходимые, не "всё подряд"

## Native~/Plugins/

- Исходники нативных библиотек: `Native~/` (тильда скрывает от Unity)
- Скомпилированные .dll/.so/.dylib: `Plugins/` с метаданными платформ
- CI собирает Native~ → Plugins/

## Namespace

Конвенция: `Company.PackageName` (напр. `SashaRX.PrefabDoctor`, `SashaRX.MeshLab`)
- Авторский префикс обязателен
- НЕ голое имя инструмента (плохо: `LightmapUvTool`, `AssetLockBoard`)

## CHANGELOG

Формат: [Keep a Changelog](https://keepachangelog.com/)
```markdown
## [Unreleased]

## [1.0.0] - 2026-04-12

### Added
- Описание фичи

### Fixed
- Описание бага
```
