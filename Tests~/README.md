# Bridge regression checks

Use a disposable Unity 6 project with this package installed. Copy
`BridgeRegressionRunner.cs` to its `Assets/Editor` directory, then run:

```powershell
& '<Unity.exe>' -batchmode -nographics -projectPath '<test project>' -executeMethod BridgeRegressionRunner.Run -logFile '<test project>/tests.log'
```

The runner creates a fresh `Assets/BridgeRegression_<timestamp>` folder, writes
`regression-results.txt`, and exits with code 0 on success or 1 on failure.
It uses Unity's FBX exporter/importer and the actual bridge apply path. Returned
FBX edits are generated through the FBX SDK, without requiring Max.

Checks:

- Source FBX: edit geometry, restore baseline, apply again; verify vertex positions.
- Unity primitive: promote to FBX, restore baseline, apply again.
- Standalone addition to a prefab with two FBX models: verify separate file location,
  prefab mesh count, and unchanged source files.
- Child addition to one of two models: verify the chosen source FBX and untouched other model.
- Child addition to a directly assigned MeshFilter.
- Each addition scenario also adds another child under the previously registered mesh.
- Two independent empty material slots survive mesh promotion and resend.
- Renderer material arrays shorter than the mesh submesh count.
- Returned FBX with missing materials, unassigned (-1) and sparse material IDs.
- Fresh, stale, malformed and missing Max heartbeat responses.
- Invalid session JSON is reported by polling without escaping or leaving it busy.
- Standalone package/assembly identity, generated connector paths and UTF-8 encoding.
- English/Russian Unity language selection, including error messages.
- Multiple material drops, duplicate filtering and queuing without a Max session.
- Repeated session-file writes and temporary-file cleanup.
- Next-update target registry, source-folder selection and newly applied mesh registration.

With the current connector loaded in Max, run `MaxUpdatePreview.ms` and call
`kcBridgeRunPreviewTests "<report file>"`. Its temporary fixtures verify standalone
and linked additions, duplicate IDs, children of new meshes, registry refresh,
and incomplete metadata. The test restores the prior session, selection and
scene dirty flag and does not reset or export the artist's scene.

For a real Max roundtrip, run `BridgeRegressionRunner.PrepareMax`, read the
session directory in `<test project>/max-session.json`, and import it with the
current connection script's `kcBridgeImport`. Scale the mesh X by 2 in one Undo
operation and export with `kcBridgeExport`. Run `BridgeRegressionRunner.ApplyMaxEdit`.
Undo that scaling in Max and export again; run `BridgeRegressionRunner.ApplyMaxUndo`.
These methods check that the imported mesh width becomes 2 and returns to 1,
writing results to `max-results.txt`. Keep export operations outside the modeling
Undo record. The test project must be closed between batch invocations.

Validated on Unity 6000.0.48f1 / FBX Exporter 5.1.3 and 3ds Max 2024.

## Русский

Используйте отдельный тестовый проект Unity 6. Команда выше запускает regression-проверки, создаёт тестовые assets и завершает Unity с кодом 0 при успехе или 1 при ошибке. Итог — `regression-results.txt`. Автоматический вариант создания проекта описан в [CONTRIBUTING.md](../CONTRIBUTING.md).

Проверяются обмен FBX, отмена правок, добавление мешей, пустые material ID, heartbeat, ошибочные сессии, генерация подключения, смена языка, очередь материалов и запись файлов сессии. Генерация ответных FBX через SDK не требует запущенного Max. Для реального обмена используйте методы PrepareMax / ApplyMaxEdit / ApplyMaxUndo, описанные выше.
