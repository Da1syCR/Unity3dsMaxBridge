using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Unity3dsMaxBridge.Editor
{
    [InitializeOnLoad]
    internal static class MaxBridgeAutoSync
    {
        private static double nextPoll;
        private static string attempted;
        private static bool busy;
        internal static string LastError { get; private set; }
        static MaxBridgeAutoSync() { EditorApplication.update += Poll; }
        private static void WriteWaitingStatus(BridgeSession session, string message)
        {
            var status = Path.Combine(session.directory, "sync.status");
            if (!File.Exists(status) || File.ReadAllText(status) != message)
            {
                File.WriteAllText(status, message);
                session.syncState = "waiting";
                session.syncMessage = message;
                session.syncUtc = DateTime.UtcNow.ToString("O");
                MaxBridgeWindow.Save(session);
                BridgeLog.Write(session, "sync.wait", message);
            }
        }
        internal static void Poll()
        {
            if (busy || EditorApplication.timeSinceStartup < nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            nextPoll = EditorApplication.timeSinceStartup + 0.5;
            try { PollCore(); LastError = ""; }
            catch (Exception e)
            {
                var error = BridgeLocalization.Text("Ошибка чтения или записи обмена: ") + e.Message;
                if (LastError != error) { Debug.LogWarning("3ds Max Bridge: " + error); BridgeLog.Write(null, "sync.io-error", error, e); }
                LastError = error;
            }
        }
        private static void PollCore()
        {
            var path = EditorPrefs.GetString(MaxBridgeWindow.SessionPreference, "");
            if (!File.Exists(path)) return;
            var session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(path));
            if (session == null || string.IsNullOrEmpty(session.id) || string.IsNullOrEmpty(session.directory) || string.IsNullOrEmpty(session.sourcePath))
                throw new InvalidDataException(BridgeLocalization.Text("Файл сессии пуст или повреждён. Отправь prefab в Max заново."));
            var ready = Path.Combine(session.directory, "Edited.ready");
            var manual = Path.Combine(session.directory, "apply.request");
            if (!File.Exists(ready)) return;
            var revision = File.ReadAllText(ready);
            var key = session.id + ":" + revision;
            if (string.IsNullOrEmpty(revision) || session.lastRevision == revision || session.lastAttemptedRevision == revision || attempted == key) return;
            if (!session.autoSync && !File.Exists(manual))
            {
                WriteWaitingStatus(session, BridgeLocalization.Text("Экспорт получен, но автоматическое применение отключено. Нажми «Получить изменения из Max». "));
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                WriteWaitingStatus(session, BridgeLocalization.Text("Обновление ожидает выхода из Play Mode."));
                return;
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == session.sourcePath && stage.scene.isDirty)
            {
                WriteWaitingStatus(session, BridgeLocalization.Text("Сохрани открытый prefab в Unity: обновление из Max ожидает сохранения."));
                return;
            }
            attempted = key;
            busy = true;
            try
            {
                // Persist before touching assets. A domain reload or editor crash
                // must not silently retry the same failed revision on startup.
                session.lastAttemptedRevision = revision;
                session.syncState = "applying";
                session.syncMessage = BridgeLocalization.Text("Unity проверяет и применяет полученную геометрию.");
                session.syncUtc = DateTime.UtcNow.ToString("O");
                MaxBridgeWindow.Save(session);
                BridgeLog.Write(session, "sync.start", "revision=" + revision + "; manual=" + File.Exists(manual));
                SourceFbxBridge.Apply(session);
                session.lastRevision = revision;
                session.syncState = "success";
                session.syncUtc = DateTime.UtcNow.ToString("O");
                session.syncMessage = session.lastApplySummary ?? BridgeLocalization.Text("Обновление завершено. Ссылки Unity проверены.");
                File.WriteAllText(Path.Combine(session.directory, "sync.status"), session.syncMessage);
                if (File.Exists(manual)) File.Delete(manual);
                BridgeLog.Write(session, "sync.success", "revision=" + revision);
            }
            catch (Exception e)
            {
                session.syncState = "error";
                session.syncUtc = DateTime.UtcNow.ToString("O");
                session.syncMessage = BridgeLocalization.Text("Обновление не применено: ") + e.Message;
                File.WriteAllText(Path.Combine(session.directory, "sync.status"), session.syncMessage);
                Debug.LogWarning("3ds Max Bridge: " + e.Message);
                BridgeLog.Write(session, "sync.error", "revision=" + revision, e);
            }
            finally
            {
                try
                {
                    MaxBridgeWindow.Save(session);
                    foreach (var window in Resources.FindObjectsOfTypeAll<MaxBridgeWindow>()) window.ReloadSession();
                }
                finally { busy = false; }
            }
        }
    }
}
