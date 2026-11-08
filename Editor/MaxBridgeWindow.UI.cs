using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Unity3dsMaxBridge.Editor
{
    public sealed partial class MaxBridgeWindow
    {
        [SerializeField] private bool showMaterials, showSettings, showDetails;
        private MessageType messageType = MessageType.Info;
        private BridgeConnectionStatus connection = new BridgeConnectionStatus();
        private double nextStatusRefresh;
        private string diskStatus = "", statusReadError = "";
        private GUIStyle heading, card, body, primary, content;

        private void OnInspectorUpdate()
        {
            if (EditorApplication.timeSinceStartup < nextStatusRefresh) return;
            nextStatusRefresh = EditorApplication.timeSinceStartup + .75;
            connection = BridgeConnectionStatus.Read(BridgeDirectory);
            try
            {
                var previousEvent = session?.syncUtc;
                ReloadSession();
                if (session?.syncUtc != previousEvent && !string.IsNullOrEmpty(session?.syncUtc)) message = null;
                var status = session == null ? null : Path.Combine(session.directory, "sync.status");
                diskStatus = status != null && File.Exists(status) ? File.ReadAllText(status) : "";
                statusReadError = "";
            }
            catch (Exception e) { statusReadError = BridgeLocalization.Text("Не удалось прочитать состояние сессии: ") + e.Message; }
            TrySendPendingMaterials();
            if (activeTab == 1) RefreshBackupStorage();
            Repaint();
        }

        private void DrawBridgeGUI()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                EditorGUILayout.HelpBox(BridgeLocalization.Text("Unity3dsMaxBridge работает с Unity Editor и 3ds Max на Windows."), MessageType.Info);
                return;
            }
            if (heading == null)
            {
                heading = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, margin = new RectOffset(0, 0, 0, 4) };
                body = new GUIStyle(EditorStyles.wordWrappedLabel) { richText = false };
                card = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(0, 0, 0, 5) };
                primary = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold, fixedHeight = 26 };
                content = new GUIStyle { padding = new RectOffset(8, 8, 6, 6) };
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.BeginVertical(content);
            var sameSession = session != null && connection.responding && connection.sessionId == session.id;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Unity ↔ 3ds Max", heading);
            var language = EditorGUILayout.Popup(BridgeLocalization.IsRussian ? 1 : 0, BridgeLocalization.Languages, GUILayout.Width(76));
            if ((language == 1) != BridgeLocalization.IsRussian)
            {
                BridgeLocalization.IsRussian = language == 1;
                Repaint();
            }
            GUILayout.FlexibleSpace();
            var dot = GUILayoutUtility.GetRect(8, 18, GUILayout.Width(8));
            EditorGUI.DrawRect(new Rect(dot.x, dot.y + 5, 7, 7), sameSession ? new Color(.25f, .8f, .5f) : new Color(1f, .68f, .25f));
            var status = !connection.responding ? BridgeLocalization.Text("Нет ответа") : sameSession ? BridgeLocalization.Text("Подключено") : BridgeLocalization.Text("Max открыт");
            if (connection.responding && connection.state == "importing") status = BridgeLocalization.Text("Импорт…");
            if (connection.responding && connection.state == "exporting") status = BridgeLocalization.Text("Экспорт…");
            GUILayout.Label(new GUIContent(status, connection.responding ? BridgeLocalization.Text("Ctrl+S в Max: ") + (connection.autoUpdate ? BridgeLocalization.Text("включён") : BridgeLocalization.Text("выключен")) : connection.problem), EditorStyles.miniLabel);
            if (!connection.responding && GUILayout.Button(new GUIContent(BridgeLocalization.Text("Подключить"), connection.problem), EditorStyles.miniButton, GUILayout.Width(85))) Run(PrepareConnection);
            EditorGUILayout.EndHorizontal();

            var nextTab = GUILayout.Toolbar(activeTab, new[] { BridgeLocalization.Text("Обмен"), BridgeLocalization.Text("Резервные копии") });
            if (nextTab != activeTab) { activeTab = nextTab; scroll = Vector2.zero; if (activeTab == 1) nextBackupScan = 0; }
            GUILayout.Space(5);
            if (activeTab == 1)
            {
                DrawBackupsGUI();
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.BeginVertical(card);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(BridgeLocalization.Text("Префаб"), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(ResolveSourcePath(source)) || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button(BridgeLocalization.Text("Независимая копия"), EditorStyles.miniButton, GUILayout.Width(145))) Run(CreateIndependentCopy);
            EditorGUILayout.EndHorizontal();
            source = (GameObject)EditorGUILayout.ObjectField(source, typeof(GameObject), true, GUILayout.Height(36));
            var sourcePath = ResolveSourcePath(source);
            if (source != null && string.IsNullOrEmpty(sourcePath)) EditorGUILayout.HelpBox(SourceSelectionError(source), MessageType.Warning);
            GUILayout.Space(4);
            DrawMaterialDropField();
            GUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            var previousColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.4f, .8f, .83f);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(sourcePath) || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button(BridgeLocalization.Text("Отправить префаб"), primary)) Run(() => Send(source));
            GUI.backgroundColor = previousColor;
            using (new EditorGUI.DisabledScope(session == null || !sameSession || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button(BridgeLocalization.Text("Получить изменения"), GUILayout.Height(26))) Run(RequestUpdate);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            DrawSyncResult();

            EditorGUILayout.BeginVertical(card);
            showMaterials = EditorGUILayout.Foldout(showMaterials, BridgeLocalization.Text("Пустые слоты"), true);
            if (showMaterials) DrawMaterialSlots();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(card);
            showSettings = EditorGUILayout.Foldout(showSettings, BridgeLocalization.Text("Настройки"), true);
            if (showSettings)
            {
                if (GUILayout.Button(BridgeLocalization.Text("Подготовить подключение Max"))) Run(PrepareConnection);
                var nextAuto = EditorGUILayout.ToggleLeft(BridgeLocalization.Text("Автоматически применять экспорт из Max"), session?.autoSync ?? autoSync);
                if (session != null && nextAuto != session.autoSync) { session.autoSync = nextAuto; Run(SaveSession); }
                autoSync = nextAuto;
                GUILayout.Label(BridgeLocalization.Text("Для экспорта по Ctrl+S включи Update Unity on Ctrl+S в окне Max. Update now работает вручную."), body);
                var nextColliders = EditorGUILayout.ToggleLeft(BridgeLocalization.Text("Создавать MeshCollider для новых мешей"), meshColliders);
                if (nextColliders != meshColliders) { meshColliders = nextColliders; if (session != null) { session.meshColliders = nextColliders; Run(SaveSession); } }
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(card);
            showDetails = EditorGUILayout.Foldout(showDetails, BridgeLocalization.Text("Диагностика и файлы"), true);
            if (showDetails)
            {
                if (session != null)
                {
                    GUILayout.Label(session.sourcePath, body);
                    foreach (var file in session.fbxFiles) GUILayout.Label("FBX  " + file.path, body);
                    GUILayout.Label(BridgeLocalization.Text("Защищённых мешей: ") + (session.looseMeshes?.Count ?? 0), body);
                    if (GUILayout.Button(BridgeLocalization.Text("Выделить активный prefab"))) Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(session.sourcePath);
                    if (GUILayout.Button(BridgeLocalization.Text("Открыть папку сессии"))) EditorUtility.RevealInFinder(Path.Combine(session.directory, "session.json"));
                    if (GUILayout.Button(BridgeLocalization.Text("Открыть журнал Unity"))) Run(() => { BridgeLog.Write(session, "log.open", "Opened by user"); EditorUtility.RevealInFinder(BridgeLog.PathFor(session)); });
                }
                if (GUILayout.Button(BridgeLocalization.Text("Скопировать диагностику"))) EditorGUIUtility.systemCopyBuffer = DiagnosticText();
                if (GUILayout.Button(BridgeLocalization.Text("Открыть папку подключения"))) Run(() => { Directory.CreateDirectory(BridgeDirectory); EditorUtility.RevealInFinder(BridgeDirectory); });
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
        }

        private void DrawMaterialDropField()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(materialSendFailed ? BridgeLocalization.Text("Материалы · ошибка отправки") : materialsPending ? BridgeLocalization.Text("Материалы · ожидают Max") : BridgeLocalization.Text("Материалы"), EditorStyles.boldLabel);
            if (materialSendFailed && GUILayout.Button(BridgeLocalization.Text("Повторить"), EditorStyles.miniButton, GUILayout.Width(80)))
            {
                materialSendFailed = false;
                TrySendPendingMaterials();
            }
            EditorGUILayout.EndHorizontal();
            var area = GUILayoutUtility.GetRect(0, 38, GUILayout.ExpandWidth(true));
            GUI.Box(area, extraMaterials.Count == 0 ? BridgeLocalization.Text("Перетащите материалы сюда") : BridgeLocalization.Text("Перетащите ещё материалы · ") + extraMaterials.Count, EditorStyles.helpBox);
            var evt = Event.current;
            if (area.Contains(evt.mousePosition) && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
            {
                var valid = DragAndDrop.objectReferences.OfType<Material>().Any(EditorUtility.IsPersistent);
                DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (valid && evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    Run(() => AddMaterials(DragAndDrop.objectReferences));
                }
                evt.Use();
            }
            for (var i = 0; i < extraMaterials.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(extraMaterials[i], typeof(Material), false);
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22)))
                {
                    extraMaterials.RemoveAt(i--);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawSyncResult()
        {
            EditorGUILayout.BeginVertical(card);
            if (!string.IsNullOrEmpty(statusReadError)) EditorGUILayout.HelpBox(statusReadError, MessageType.Error);
            if (!string.IsNullOrEmpty(MaxBridgeAutoSync.LastError)) EditorGUILayout.HelpBox(MaxBridgeAutoSync.LastError, MessageType.Error);
            var maxError = connection.responding && connection.state == "error" && (session == null || connection.operationSessionId == session.id);
            if (maxError) EditorGUILayout.HelpBox(BridgeLocalization.Text("Ошибка в 3ds Max: ") + connection.message + BridgeLocalization.Text("\nИсправь причину в Max и нажми Update now ещё раз."), MessageType.Error);
            if (session == null) GUILayout.Label(BridgeLocalization.Text("Нет активной сессии"), EditorStyles.miniLabel);
            else
            {
                var text = session.syncMessage;
                var severity = session.syncState == "error" ? MessageType.Error : session.syncState == "waiting" ? MessageType.Warning : MessageType.Info;
                if (session.syncState == "requested" && DateTime.TryParse(session.syncUtc, out var requested) && DateTime.UtcNow - requested.ToUniversalTime() > TimeSpan.FromSeconds(15))
                { text = BridgeLocalization.Text("Экспорт ещё не получен. Проверь, что в Max открыта нужная сессия, и нажми Update now. Max может быть занят или ожидать закрытия диалога."); severity = MessageType.Warning; }
                if (string.IsNullOrEmpty(text)) text = BridgeLocalization.Text("Ожидание изменений из Max");
                if (!maxError || !string.IsNullOrEmpty(session.syncMessage))
                {
                    if (severity == MessageType.Info) GUILayout.Label(new GUIContent(text, text), EditorStyles.miniLabel);
                    else EditorGUILayout.HelpBox(text, severity);
                }
                if (session.syncState == "error") GUILayout.Label(BridgeLocalization.Text("После исправления причины снова запроси изменения. Эта неудачная попытка автоматически не повторяется."), body);
            }
            if (!string.IsNullOrEmpty(message))
            {
                if (messageType == MessageType.Info) GUILayout.Label(new GUIContent(message, message), EditorStyles.miniLabel);
                else EditorGUILayout.HelpBox(message, messageType);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawMaterialSlots()
        {
            var prefab = session == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(session.sourcePath);
            if (prefab == null) return;
            var empty = 0;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || filter.sharedMesh == null) continue;
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < Math.Max(materials.Length, filter.sharedMesh.subMeshCount); i++)
                    if (i >= materials.Length || materials[i] == null)
                    { empty++; GUILayout.Label(filter.name + BridgeLocalization.Text(" · слот ") + (i + 1) + (i >= materials.Length ? BridgeLocalization.Text(" — отсутствует в Renderer") : BridgeLocalization.Text(" — пустой")), body); }
            }
            if (empty == 0) GUILayout.Label(BridgeLocalization.Text("В активном prefab пустых слотов нет."), EditorStyles.miniLabel);
            else GUILayout.Label(BridgeLocalization.Text("Пустых слотов: ") + empty + BridgeLocalization.Text(". Это не ошибка синхронизации."), EditorStyles.miniLabel);
        }

        private string DiagnosticText() => "Unity3dsMaxBridge\nUnity: " + Application.unityVersion + "\nPrefab: " + session?.sourcePath +
            "\nSession: " + session?.id + "\nMax responding: " + connection.responding + "\nMax session: " + connection.sessionId +
            "\nMax state: " + connection.state + "\nMax message: " + connection.message + "\nConnection: " + connection.problem +
            "\nSync: " + session?.syncState + "\nResult: " + session?.syncMessage + "\nStatus file: " + diskStatus +
            "\nWindow: " + message + "\nRead error: " + statusReadError + "\nPoll error: " + MaxBridgeAutoSync.LastError + "\nFolder: " + session?.directory;
    }
}
