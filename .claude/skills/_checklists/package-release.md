# Чеклист: релиз пакета

Проверяй перед каждым релизом / git tag.

## Метаданные
- [ ] `package.json`: version обновлён (semver — MAJOR.MINOR.PATCH)
- [ ] `package.json`: `unity` = минимальная поддерживаемая LTS
- [ ] `package.json`: `repository.url` совпадает с реальным URL репозитория
- [ ] `package.json`: нет нестандартных полей (`type`, `main`, `module`)
- [ ] `package.json`: `dependencies` актуальны (не забыты, не лишние)

## Документация
- [ ] `CHANGELOG.md` обновлён (формат Keep a Changelog — Added/Changed/Fixed/Removed)
- [ ] `README.md` актуален (установка, использование, требования)
- [ ] `LICENSE` присутствует и совпадает с `license` в package.json

## Код
- [ ] asmdef: `name` соответствует `Company.PackageName.Editor`, `includePlatforms` корректен
- [ ] Namespace единообразен с авторским префиксом (`Company.PackageName`)
- [ ] Нет файлов >100 КБ без обоснования (кандидаты на декомпозицию)
- [ ] Нет захардкоженных путей (`Assets/...`), URL, magic strings
- [ ] Нет `Resources.Load` в Editor-коде
- [ ] Все мутации Unity Object обёрнуты в Undo

## Тесты
- [ ] `Tests/Editor/` существует с минимум smoke-тестами
- [ ] Тесты проходят (`Window → General → Test Runner → Run All`)

## Сборка
- [ ] Компиляция без ошибок в минимальной версии Unity из package.json
- [ ] Компиляция без ошибок в последней LTS
- [ ] `#if UNITY_*` гейты корректны — фолбэки работают

## Публикация
- [ ] git tag совпадает с версией в package.json (напр. `v1.0.0`)
- [ ] Установка через git URL работает
- [ ] Установка через диск работает
