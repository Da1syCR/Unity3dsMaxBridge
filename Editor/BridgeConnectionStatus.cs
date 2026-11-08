using System;
using System.IO;

namespace Unity3dsMaxBridge.Editor
{
    internal sealed class BridgeConnectionStatus
    {
        internal bool responding;
        internal string sessionId = "", operationSessionId = "", state = "", message = "", problem = "";
        internal bool autoUpdate;
        internal DateTime lastResponseUtc;
        internal static BridgeConnectionStatus Read(string directory)
        {
            var result = new BridgeConnectionStatus();
            var path = Path.Combine(directory, "max.heartbeat");
            try
            {
                if (!File.Exists(path)) { result.problem = BridgeLocalization.Text("Нет ответа от bridge в Max. Выполни Connect_Max.ms в 3ds Max."); return result; }
                result.lastResponseUtc = File.GetLastWriteTimeUtc(path);
                var lines = File.ReadAllLines(path);
                if (lines.Length != 7 || lines[0] != "KCB_HEARTBEAT_1" || lines[6] != "KCB_END")
                { result.problem = BridgeLocalization.Text("Ответ Max не распознан. Подготовь подключение и выполни новый Connect_Max.ms."); return result; }
                result.sessionId = lines[1]; result.state = lines[2]; result.message = lines[3];
                result.autoUpdate = lines[4] == "true"; result.operationSessionId = lines[5];
                result.responding = DateTime.UtcNow - result.lastResponseUtc < TimeSpan.FromSeconds(8);
                if (!result.responding) result.problem = BridgeLocalization.Text("Max не отвечает более 8 секунд: он может быть занят, закрыт или окно bridge отключено.");
            }
            catch (Exception e) { result.problem = BridgeLocalization.Text("Не удалось прочитать ответ Max: ") + e.Message; }
            return result;
        }
    }
}
