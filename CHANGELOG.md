# Changelog / История изменений

## 0.2.0-beta.3 — 2026-11-09

- Added a Unity Backups tab with folder-opening buttons and sizes for snapshots and initial source copies, grouped by session.
- Shows combined backup size for the project; background scanning excludes exchange exports/logs and reports incomplete reads.
- Added size-accounting regression coverage, including nested folders and Unicode prefab names.

- В Unity добавлена вкладка резервных копий: открытие папок, размеры снимков изменений и исходных копий по сессиям.
- Общий размер проекта считается в фоне, без файлов обмена и журналов; неполный подсчёт отмечается предупреждением.
- Добавлена проверка размеров, вложенных папок и имён prefab с кириллицей.

## 0.2.0-beta.2 — 2026-11-09

- Max's Next update panel previews which new meshes create separate FBX files and which target an existing source FBX.
- Preview follows Unity's parent/source mapping, shows the destination folder, and warns about reference validation and protected Unity meshes.
- Preview is read-only and refreshes after Unity registers an applied mesh. Added Unity and Max regression coverage.

- Панель Next update в Max показывает новые отдельные FBX и добавления в исходный FBX.
- Предпросмотр учитывает привязку к модели, показывает папку назначения и поясняет проверки ссылок и защищённые меши Unity.
- Просмотр не меняет сцену и обновляется после регистрации меша в Unity. Добавлены проверки Unity и Max.

## 0.2.0-beta.1 — 2026-11-09

First public beta of the standalone MIT-licensed package.

- Git URL and ZIP/disk installation; FBX Exporter dependency declared in UPM.
- Standalone package, namespace, assembly, settings and exchange directory.
- English / Russian Unity interface and messages; English-only Max interface.
- Compact prefab/material drop fields, queued automatic material delivery and independent prefab copies.
- Max toolbar launcher and persistent action for custom toolbars/hotkeys.
- Source FBX updates, material assignments, backups, validation and rollback.
- English/Russian documentation, portable source checks and a disposable-project regression runner.
- Unicode connector generation and source-volume staging without drive-root permissions.

Первый публичный beta-выпуск отдельного пакета под MIT.

- Установка через Git URL или ZIP/package.json с зависимостью FBX Exporter.
- Самостоятельные имена пакета, сборки, namespace, настроек и папки обмена.
- English / Русский в Unity; английский интерфейс Max.
- Компактные поля prefab/материалов, очередь автоотправки и независимые копии.
- Кнопка Max и сохраняемая команда для своих панелей и горячих клавиш.
- Обновление исходных FBX и назначений материалов, резервные копии, проверки и откат.
- Документация на двух языках, проверки исходников и запуск тестов в отдельном проекте.
- Поддержка Unicode в подключении и временная запись моделей без доступа к корню диска.

Verified versions are documented in README. This is a beta; unsupported geometry and lifecycle limits are listed there.
Проверенные версии и ограничения beta перечислены в README.ru.md.
