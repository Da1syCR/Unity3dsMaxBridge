# Contributing / Разработка

## English

Use a separate checkout and a disposable Unity 6 project. Runtime code is Editor-only. Do not commit exported models, user paths, logs, session data or dependency binaries. Keep Max's interface English and localize Unity messages through `BridgeLocalization`.

Run portable source checks with Python 3:

```sh
python Tools~/validate_release.py
```

Run the Unity regression suite using an Editor you have installed and licensed:

```sh
python Tools~/run_regression.py --unity "<Unity Editor executable>" --unity-version 6000.0.48f1 --project "<new disposable project folder>"
```

The helper refuses to overwrite an existing nonempty project. It installs this checkout and dependencies, copies the runner from `Tests~`, and returns Unity's exit code. Results are in `regression-results.txt`; the full log is `regression.log` in that disposable project. This suite tests Unity/FBX processing; it does not replace manual tests in a running Max.

Before a release, also test a clean install, both Unity languages, the generated connector in Max, reopening its window through the toolbar, Ctrl+S with auto-export on/off, material delivery, and switching the active project. Record exact Unity and Max versions. Publish only the package source and its metadata. Update the version, both install URLs and changelog together.

## Русский

Работай в отдельной копии репозитория и тестовом Unity-проекте. Не добавляй в Git экспортированные модели, персональные пути, журналы, сессии или бинарные зависимости. Интерфейс Max остаётся английским; сообщения Unity переводятся через `BridgeLocalization`.

Первая команда выше проверяет пакет без Unity. Вторая создаёт новый тестовый проект, устанавливает пакет и зависимости и запускает regression-тесты. Нужен установленный и лицензированный Unity 6. Существующая непустая папка проекта не перезаписывается. Результаты — `regression-results.txt`, журнал — `regression.log` внутри созданного проекта.

Перед выпуском вручную проверь установку, оба языка Unity, подключение Max, кнопку повторного открытия, Ctrl+S с включённым и выключенным автоэкспортом, передачу материалов и смену проекта. Зафиксируй версии Unity/Max. Тесты Unity не заменяют проверку в работающем Max. Версия пакета, ссылки установки и история изменений обновляются вместе.
