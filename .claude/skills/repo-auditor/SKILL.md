---
name: repo-auditor
description: Используй при запросе аудита, сканирования или отчёта о здоровье Unity UPM-пакета. Только отчёт, без правок.
---

# Repo Auditor

Сканируй репозиторий и выдавай структурированный отчёт. НЕ пиши фиксы — только диагностика.

## Чеклист проверки

1. **package.json** — name (com.company.*), version (semver), unity (LTS?), repository.url (совпадает с реальным URL?), нестандартные поля (type, main и т.д.)
2. **asmdef** — имя соответствует `Company.PackageName.Editor`, `includePlatforms: ["Editor"]` для Editor-only, лишние references, `allowUnsafeCode` только если есть unsafe
3. **Структура** — Editor/, Runtime/ (только если runtime-код), Tests/Editor/, Documentation~/, Samples~/
4. **Namespace** — единый, с авторским префиксом (например `SashaRX.PackageName`), не голое имя инструмента
5. **Размеры файлов** — файлы >50 КБ = кандидаты на декомпозицию (пример: unitymeshlab/GroupedShellTransfer.cs — 213 КБ)
6. **README.md / CHANGELOG.md** — наличие, CHANGELOG в формате Keep a Changelog
7. **LICENSE** — наличие, совпадение с полем license в package.json
8. **CI** — наличие .github/workflows/ для Unity test runner (EditMode/PlayMode)
9. **Антипаттерны в коде:**
   - Ручной JSON-парсинг строковыми операциями (IndexOf, Substring для JSON)
   - `.mesh` вместо `.sharedMesh` в Editor-коде
   - `AssetDatabase.StartAssetEditing()` без `try/finally`
   - `DestroyImmediate` на сценных объектах без Undo
   - Мутация Unity Object без `Undo.RecordObject`
   - Прямая мутация `target` в CustomEditor без SerializedObject
   - Editor-код в Runtime asmdef
   - `Resources.Load` в Editor-коде
   - Захардкоженные пути (`Assets/...`) и URL
   - `UnityWebRequest` без timeout

## Формат отчёта

```markdown
## Аудит: [имя пакета] v[версия]

| Проверка | Статус | Детали |
|----------|--------|--------|
| package.json | OK / WARN / FAIL | описание |
| asmdef | OK / WARN / FAIL | описание |
| Структура | OK / WARN / FAIL | описание |
| ... | ... | ... |

### Приоритизированные рекомендации

1. **CRITICAL:** [описание] — файл:строка
2. **HIGH:** [описание]
3. **LOW:** [описание]
```

## Правила

- Не предлагай фиксы — только выявляй и описывай
- Цитируй конкретные файлы и строки
- Если что-то отсутствует — говори прямо ("Tests/Editor/ — отсутствует")
- Сравнивай с эталонной структурой из скилла unity-package-architect
