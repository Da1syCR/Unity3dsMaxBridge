using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Unity3dsMaxBridge.Editor
{
    public sealed partial class MaxBridgeWindow
    {
        [SerializeField] private int activeTab;
        private Task<BridgeBackupStorage.Report> backupScan;
        private BridgeBackupStorage.Report backupReport;
        private double nextBackupScan;
        private string backupScanError;

        private void RefreshBackupStorage()
        {
            if (backupScan != null)
            {
                if (!backupScan.IsCompleted) return;
                try { backupReport = backupScan.GetAwaiter().GetResult(); backupScanError = null; }
                catch (Exception e) { backupScanError = e.Message; }
                backupScan = null;
            }
            if (EditorApplication.timeSinceStartup < nextBackupScan) return;
            nextBackupScan = EditorApplication.timeSinceStartup + 30;
            var root = BridgeDirectory;
            backupScan = Task.Run(() => BridgeBackupStorage.Scan(root));
        }

        private void DrawBackupsGUI()
        {
            RefreshBackupStorage();
            EditorGUILayout.BeginVertical(card);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(BridgeLocalization.Text("Резервные копии проекта"), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(backupScan != null))
                if (GUILayout.Button(BridgeLocalization.Text("Обновить"), EditorStyles.miniButton, GUILayout.Width(85))) nextBackupScan = 0;
            EditorGUILayout.EndHorizontal();
            if (backupScan != null) GUILayout.Label(BridgeLocalization.Text("Подсчёт размера…"), EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(backupScanError)) EditorGUILayout.HelpBox(backupScanError, MessageType.Error);
            if (backupReport != null)
            {
                GUILayout.Label(BridgeLocalization.Text("Всего: ") + EditorUtility.FormatBytes(backupReport.totalBytes) +
                    BridgeLocalization.Text(" · файлов: ") + backupReport.totalFiles, EditorStyles.boldLabel);
                GUILayout.Label(BridgeLocalization.Text("Снимки изменений и исходные копии всех сессий этого проекта."), body);
                if (backupReport.unreadable > 0)
                    EditorGUILayout.HelpBox(BridgeLocalization.Text("Не все папки удалось прочитать. Показанный размер может быть неполным."), MessageType.Warning);
                if (backupReport.sessions.Count == 0) GUILayout.Label(BridgeLocalization.Text("Резервных копий пока нет."), body);
            }
            EditorGUILayout.EndVertical();
            if (backupReport == null) return;
            foreach (var item in backupReport.sessions)
            {
                EditorGUILayout.BeginVertical(card);
                var name = item.name + " · " + item.id.Substring(0, Math.Min(8, item.id.Length));
                if (session != null && item.id == session.id) name += BridgeLocalization.Text(" · активная");
                GUILayout.Label(name, EditorStyles.boldLabel);
                DrawBackupFolder(item.backups, BridgeLocalization.Text("Открыть резервные копии"));
                DrawBackupFolder(item.originals, BridgeLocalization.Text("Открыть исходные копии"));
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawBackupFolder(BridgeBackupStorage.Folder folder, string label)
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!folder.exists))
                if (GUILayout.Button(new GUIContent(label, folder.path), GUILayout.MinWidth(170)))
                    Run(() => {
                        if (!Directory.Exists(folder.path)) throw new DirectoryNotFoundException(BridgeLocalization.Text("Папка резервных копий больше не существует."));
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder.path, UseShellExecute = true });
                    });
            GUILayout.Label(EditorUtility.FormatBytes(folder.bytes) + BridgeLocalization.Text(" · файлов: ") + folder.files, GUILayout.Width(145));
            EditorGUILayout.EndHorizontal();
        }
    }
}
