using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Unity3dsMaxBridge.Editor
{
    public sealed partial class MaxBridgeWindow
    {
        // Max reads this small registry without parsing Unity's session JSON.
        // Refresh it after successful saves so applied additions stop appearing as new.
        private static void WriteUpdateTargets(BridgeSession value)
        {
            if (string.IsNullOrEmpty(value.sourcePath)) return;
            var firstModel = value.fbxFiles.FirstOrDefault()?.path ?? value.sourcePath;
            var directory = Path.GetDirectoryName(firstModel).Replace('\\', '/');
            if (directory != "Assets" && !directory.StartsWith("Assets/", StringComparison.Ordinal))
                directory = Path.GetDirectoryName(value.sourcePath).Replace('\\', '/');
            var lines = new List<string> { "UMB_UPDATE_TARGETS_1", "new-directory|" + directory };
            foreach (var id in value.nodes.Select(n => n.id).Concat(value.fbxBindings.Select(n => n.nodeId))
                .Concat(value.looseMeshes.Select(n => n.nodeId)).Where(id => !string.IsNullOrEmpty(id)).Distinct())
                lines.Add("known|" + id);
            foreach (var binding in value.fbxBindings)
            {
                var file = value.fbxFiles.FirstOrDefault(f => f.guid == binding.fileGuid);
                if (file != null) lines.Add("source|" + binding.nodeId + "|" + file.path);
            }
            if (value.looseMeshes.Any(n => string.IsNullOrEmpty(n.fileGuid))) lines.Add("protected|true");
            lines.Add("UMB_END");
            var text = string.Join("\n", lines) + "\n";
            var path = Path.Combine(value.directory, "UpdateTargets.txt");
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
                CommitExchangeFile(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
