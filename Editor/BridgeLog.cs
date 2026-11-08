using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Unity3dsMaxBridge.Editor
{
    internal static class BridgeLog
    {
        [Serializable] private sealed class Entry
        {
            public string utc, eventName, session, prefab, message, exception;
        }
        internal static string PathFor(BridgeSession session) => Path.Combine(session == null ? MaxBridgeWindow.BridgeDirectory : session.directory, "unity-bridge.jsonl");

        internal static void Write(BridgeSession session, string eventName, string message, Exception exception = null)
        {
            try
            {
                var path = PathFor(session);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    if (File.Exists(path + ".1")) File.Delete(path + ".1");
                    File.Move(path, path + ".1");
                }
                var entry = new Entry { utc = DateTime.UtcNow.ToString("O"), eventName = eventName,
                    session = session?.id ?? "", prefab = session?.sourcePath ?? "", message = message,
                    exception = exception?.ToString() ?? "" };
                File.AppendAllText(path, JsonUtility.ToJson(entry) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogWarning(BridgeLocalization.Text("Max Bridge: не удалось записать журнал: ") + e.Message); }
        }
    }
}
