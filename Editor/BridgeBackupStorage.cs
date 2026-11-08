using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Unity3dsMaxBridge.Editor
{
    // Filesystem-only code: safe to run on a worker without touching Unity APIs.
    internal static class BridgeBackupStorage
    {
        internal sealed class Folder
        {
            internal string path;
            internal long bytes;
            internal int files, unreadable;
            internal bool exists;
        }
        internal sealed class Session
        {
            internal string id, name;
            internal Folder backups, originals;
            internal DateTime updatedUtc;
        }
        internal sealed class Report
        {
            internal readonly List<Session> sessions = new List<Session>();
            internal long totalBytes;
            internal int totalFiles, unreadable;
        }

        internal static Report Scan(string root)
        {
            var result = new Report();
            try { if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) return result; }
            catch (FileNotFoundException) { return result; }
            catch (DirectoryNotFoundException) { return result; }
            catch (UnauthorizedAccessException) { result.unreadable++; return result; }
            catch (IOException) { result.unreadable++; return result; }
            try
            {
                foreach (var path in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        var backups = Measure(Path.Combine(path, "Backups"));
                        var originals = Measure(Path.Combine(path, "InitialSources"));
                        result.unreadable += backups.unreadable + originals.unreadable;
                        if (backups.files + originals.files == 0 && backups.unreadable + originals.unreadable == 0) continue;
                        var id = Path.GetFileName(path);
                        var name = id.Substring(0, Math.Min(8, id.Length));
                        var json = Path.Combine(path, "session.json");
                        if (File.Exists(json))
                        {
                            try
                            {
                                string text;
                                using (var stream = new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
                                var match = Regex.Match(text, "\"sourcePath\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                                if (match.Success) name = Path.GetFileNameWithoutExtension(Regex.Unescape(match.Groups[1].Value).Replace('\\', '/'));
                            }
                            catch (IOException) { result.unreadable++; }
                            catch (UnauthorizedAccessException) { result.unreadable++; }
                            catch (ArgumentException) { }
                        }
                        result.sessions.Add(new Session { id = id, name = name, backups = backups, originals = originals, updatedUtc = Directory.GetLastWriteTimeUtc(path) });
                        result.totalBytes += backups.bytes + originals.bytes;
                        result.totalFiles += backups.files + originals.files;
                    }
                    catch (IOException) { result.unreadable++; }
                    catch (UnauthorizedAccessException) { result.unreadable++; }
                }
            }
            catch (IOException) { result.unreadable++; }
            catch (UnauthorizedAccessException) { result.unreadable++; }
            result.sessions.Sort((a, b) => b.updatedUtc.CompareTo(a.updatedUtc));
            return result;
        }

        private static Folder Measure(string path)
        {
            var result = new Folder { path = path };
            try { result.exists = (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
            catch (FileNotFoundException) { return result; }
            catch (DirectoryNotFoundException) { return result; }
            catch (UnauthorizedAccessException) { result.unreadable++; return result; }
            catch (IOException) { result.unreadable++; return result; }
            if (!result.exists) return result;
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                            else { result.bytes += new FileInfo(entry).Length; result.files++; }
                        }
                        catch (IOException) { result.unreadable++; }
                        catch (UnauthorizedAccessException) { result.unreadable++; }
                    }
                }
                catch (IOException) { result.unreadable++; }
                catch (UnauthorizedAccessException) { result.unreadable++; }
            }
            return result;
        }
    }
}
