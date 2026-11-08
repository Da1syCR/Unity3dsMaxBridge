# Unity3dsMaxBridge

Edit static Unity prefab geometry in 3ds Max and bring it back into the original FBX assets, preserving Unity references and material assignments.

**Public beta · Windows · MIT** · [Русская инструкция](README.ru.md)

## Install

### With Git

1. Open **Window → Package Manager** in Unity.
2. Click **+ → Install package from git URL** and paste:

   ```text
   https://github.com/Da1syCR/Unity3dsMaxBridge.git#v0.2.0-beta.1
   ```

3. Wait for Unity to install the package and its FBX Exporter dependency.
4. Open **Tools → Unity3dsMaxBridge → Open**.

Git must be installed and available in PATH for this method. See [Unity's Git installation instructions](https://docs.unity3d.com/6000.0/Documentation/Manual/upm-ui-giturl.html).

### Without Git

Download the [source ZIP](https://github.com/Da1syCR/Unity3dsMaxBridge/archive/refs/tags/v0.2.0-beta.1.zip), extract it to a permanent folder, then choose **Package Manager → + → Install package from disk** and select its `package.json`. Keep the extracted folder while the package is installed.

Use one installation method per project. Remove an earlier Assets copy before installing the UPM package.

## Connect 3ds Max

1. In the Unity bridge window, click **Connect** or **Settings → Prepare Max connection**.
2. Unity reveals the generated `Connect_Max.ms` and copies its path to the clipboard.
3. In Max, choose **Scripting → Run Script**, paste that path and run it.

Max adds a **Unity Bridge** toolbar button. It reopens the bridge window without creating a second session. You can also assign the action from category **Unity3dsMaxBridge** to your own toolbar or hotkey. The saved action loads the last prepared connection after restarting Max.

No Unity/Max executable path, administrator access, MCP server, or Python installation is needed to use the plugin. Max imports into the current scene; the connector does not reset or save it. Keep the Max bridge window open while synchronizing.

## Use

1. Drop a saved prefab or its scene instance into **Prefab**. Use **Independent copy** first when you want to edit a separate version of shared models.
2. Click **Send prefab**.
3. Edit geometry in Max and click **Update now**. For export on Ctrl+S, enable **Update Unity on Ctrl+S** in Max; this option starts disabled.
4. Unity validates the returned geometry and applies it automatically when enabled. An unsaved Prefab Mode or Play Mode defers the update.

Drop one or several existing Unity materials into **Materials**. The bridge sends them automatically when Max has the matching session open; otherwise it queues them. Assign them from the **Unity materials** list in Max. Duplicate drops are ignored.

Unity offers **English / Русский** in the window header and remembers your choice. Max stays in English.

## Geometry and materials

- Existing geometry updates its original FBX. Other prefabs using that FBX also see the change.
- A new standalone Max mesh gets a separate FBX beside the first source model, or beside the prefab when needed.
- **Select and Link** can add a new mesh to an existing model's FBX. If that changes existing Unity fileIDs, the bridge rejects the update and restores files; unlink the new mesh and export it as a standalone object.
- **Attach** updates an existing mesh. Undo in Max followed by **Update now** can return it to its earlier geometry.
- Material assignments map back to Unity assets. Arbitrary Max shaders and texture edits are not converted into Unity materials.
- Unregistered or empty material IDs remain separate submeshes with empty material slots.

## Requirements and limits

Tested development configuration: **Unity 6000.0.48f1, 3ds Max 2024, Windows**. Other versions are not yet verified. The package declares Unity 6 and FBX Exporter 5.1.3; Autodesk FBX SDK is resolved as its dependency. Unity needs network access for the initial dependency download.

This beta supports static **MeshFilter + MeshRenderer** geometry. Skinning, blend shapes and deleting original source meshes are not supported. Different edits to instances of one shared Unity mesh cannot become different geometries in that same mesh asset. Non-FBX Unity meshes start protected in Max; editing them can create a companion FBX.

Changes outside the bridge may require sending the prefab again. There is one active session per Unity project and one active project connection per Max instance. Before switching projects, prepare/run the intended project's connector and send its prefab.

## Backups and troubleshooting

Use **Diagnostics and files → Open session folder / Open Unity log / Copy diagnostics**. Exchange data and backups live under the current Windows user's local application data, in `Unity3dsMaxBridge/<project-key>`, outside the Unity project. Model-write staging uses a hidden `.Unity3dsMaxBridgeTemp` folder beside the model, so no drive-root permissions are needed.

Failed source-file validation triggers rollback. Backups are retained; this beta does not provide a backup-browser or one-click restore UI. Use a version-controlled or backed-up project while evaluating it.

If Max does not respond, reopen its bridge window, close blocking dialogs, or prepare and run the connector again. After updating the Unity package, run the newly generated connector before starting a new session. Save unfinished Max work separately before resending a prefab.

## Update and uninstall

Install a newer tagged Git URL (or replace the extracted ZIP folder and refresh Unity), then prepare/run the new Max connector. Remove the package through Package Manager to uninstall the Unity part.

For Max cleanup, close the bridge window and remove the Unity Bridge toolbar/action assignment. The saved macro is `Unity3dsMaxBridge-Unity3dsMaxBridge.mcr` in Max's usermacros folder; the last-connection setting is `Unity3dsMaxBridge.ini` in its user scripts folder. Restart Max after uninstalling to remove live callbacks. Keep exchange backups until you no longer need them.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for source checks and reproducible Unity regression tests. Report bugs through [GitHub Issues](https://github.com/Da1syCR/Unity3dsMaxBridge/issues), including Unity/Max versions, reproduction steps and bridge diagnostics. Review diagnostics before posting them publicly: they include local asset/session paths.

## License

[MIT](LICENSE), copyright 2026 Da1syCR. Unity, Autodesk and their separately installed packages retain their respective licenses; their binaries are not included in this repository.