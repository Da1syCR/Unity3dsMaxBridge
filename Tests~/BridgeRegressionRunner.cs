// Copy to Assets/Editor in a disposable Unity project with this package installed.
// Run: Unity -batchmode -projectPath <project> -executeMethod BridgeRegressionRunner.Run -logFile <log>
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Fbx;
using Unity3dsMaxBridge.Editor;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEngine;
using Object = UnityEngine.Object;

public static class BridgeRegressionRunner
{
    static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Type Bridge = typeof(MaxBridgeWindow).Assembly.GetType("Unity3dsMaxBridge.Editor.SourceFbxBridge");
    static readonly Type Document = Bridge.GetNestedType("Document", Flags);
    static readonly List<string> Results = new List<string>();
    static string Folder;
    static object Get(object obj, string field) => obj.GetType().GetField(field, Flags).GetValue(obj);
    static object Call(Type type, object obj, string method, params object[] args)
    {
        try { return type.GetMethod(method, Flags).Invoke(obj, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw(); throw; }
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static IEnumerable<FbxNode> Nodes(FbxNode root)
    {
        for (int i = 0; i < root.GetChildCount(); i++)
        { var n = root.GetChild(i); yield return n; foreach (var c in Nodes(n)) yield return c; }
    }
    static void Edit(string source, string destination, Action<FbxScene> edit)
    {
        var doc = Activator.CreateInstance(Document, Flags, null, new object[] { source }, null);
        try { edit((FbxScene)Get(doc, "scene")); Call(Document, doc, "Save", destination); }
        finally { ((IDisposable)doc).Dispose(); }
    }
    static object Send(GameObject prefab)
    {
        var window = ScriptableObject.CreateInstance<MaxBridgeWindow>();
        try { Call(typeof(MaxBridgeWindow), window, "Send", prefab); return Get(window, "session"); }
        finally { Object.DestroyImmediate(window); }
    }
    static string Dir(object session) => (string)Get(session, "directory");
    static void Apply(object session) => Call(Bridge, null, "Apply", session);
    static string Fixture(string label, bool loose = false, bool multiple = false, bool direct = false)
    {
        var root = new GameObject(label);
        var material = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(material, Folder + "/" + label + ".mat");
        try
        {
            for (int i = 0; i < (multiple ? 2 : 1); i++)
            {
                var model = new GameObject("Model" + i);
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Mesh" + i;
                cube.transform.SetParent(model.transform, false);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                if (loose) { cube.transform.SetParent(root.transform, false); Object.DestroyImmediate(model); continue; }
                var path = Folder + "/" + label + "_Model" + i + ".fbx";
                ModelExporter.ExportObject(path, model, new ExportModelOptions { ExportFormat = ExportFormat.Binary, ModelAnimIncludeOption = Include.Model, KeepInstances = false });
                Object.DestroyImmediate(model);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (direct)
                {
                    var child = new GameObject("Direct" + i);
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = asset.GetComponentInChildren<MeshFilter>().sharedMesh;
                    child.AddComponent<MeshRenderer>().sharedMaterial = material;
                }
                else
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, root.transform);
                    instance.transform.localPosition = new Vector3(i * 3, 0, 0);
                    foreach (var r in instance.GetComponentsInChildren<MeshRenderer>()) r.sharedMaterial = material;
                }
            }
            var prefabPath = Folder + "/" + label + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            return prefabPath;
        }
        finally { Object.DestroyImmediate(root); }
    }
    static Vector3[] Vertices(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<MeshFilter>().sharedMesh.vertices;
    static void AssertSame(Vector3[] a, Vector3[] b)
    {
        Check(a.All(v => b.Any(w => (v - w).sqrMagnitude < 1e-8f)) && b.All(v => a.Any(w => (v-w).sqrMagnitude < 1e-8f)), "Vertex positions differ");
    }
    static void UndoCase(bool loose)
    {
        var path = Fixture(loose ? "LooseUndo" : "FbxUndo", loose);
        var before = Vertices(path);
        var session = Send(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        var baseline = Path.Combine(Dir(session), "MaxBaseline.fbx");
        Edit(Path.Combine(Dir(session), "ToMax.fbx"), baseline, _ => {});
        var edited = Path.Combine(Dir(session), "Edited.fbx");
        Edit(baseline, edited, scene => {
            var mesh = Nodes(scene.GetRootNode()).First(n => n.GetMesh() != null).GetMesh();
            for (int i = 0; i < mesh.GetControlPointsCount(); i++) { var p = mesh.GetControlPointAt(i); p[0] *= 2; mesh.SetControlPointAt(p, i); }
        });
        Apply(session);
        Check(Vertices(path).Max(v => v.x) > before.Max(v => v.x) * 1.5f, "Edit did not apply");
        File.Copy(baseline, edited, true);
        Apply(session);
        AssertSame(before, Vertices(path));
        Apply(session);
        AssertSame(before, Vertices(path));
    }
    static void AdditionCase(bool child, bool direct)
    {
        var path = Fixture(child ? (direct ? "DirectChild" : "NestedChild") : "Standalone", multiple: true, direct: direct);
        var session = Send(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        var baseline = Path.Combine(Dir(session), "MaxBaseline.fbx");
        Edit(Path.Combine(Dir(session), "ToMax.fbx"), baseline, _ => {});
        var originalFiles = ((IEnumerable)Get(session, "fbxFiles")).Cast<object>().Select(f => (string)Get(f,"path")).ToArray();
        var hashes = originalFiles.Select(p => File.ReadAllBytes(p)).ToArray();
        var edited = Path.Combine(Dir(session), "Edited.fbx");
        var id = Guid.NewGuid().ToString("N");
        Edit(baseline, edited, scene => {
            var original = Nodes(scene.GetRootNode()).First(n => n.GetMesh() != null);
            var added = FbxNode.Create(scene, "Addition__KCB_" + id);
            added.SetNodeAttribute(original.GetMesh());
            for (int i = 0; i < original.GetMaterialCount(); i++) added.AddMaterial(original.GetMaterial(i));
            (child ? original : scene.GetRootNode()).AddChild(added);
            added.LclTranslation.Set(new FbxDouble3(0, 2, 0));
        });
        Apply(session);
        var binding = ((IEnumerable)Get(session,"fbxBindings")).Cast<object>().Single(b => (string)Get(b,"nodeId") == id);
        var addedPath = AssetDatabase.GUIDToAssetPath((string)Get(binding,"fileGuid"));
        Call(typeof(MaxBridgeWindow), null, "Save", session);
        var targets = File.ReadAllLines(Path.Combine(Dir(session), "UpdateTargets.txt"));
        Check(targets.Contains("known|" + id) && targets.Contains("source|" + id + "|" + addedPath), "Applied mesh is still unknown to the Max update preview");
        Check(child ? addedPath == originalFiles[0] : !originalFiles.Contains(addedPath), "Wrong destination FBX");
        Check(Path.GetDirectoryName(addedPath) == Path.GetDirectoryName(originalFiles[0]), "Wrong destination directory");
        Check(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<MeshFilter>().Length == 3, "Missing added prefab mesh");
        Check(File.ReadAllBytes(originalFiles[1]).SequenceEqual(hashes[1]), "Unrelated FBX modified");
        if (!child) Check(File.ReadAllBytes(originalFiles[0]).SequenceEqual(hashes[0]), "Standalone addition modified source FBX");
        // Add below the mesh registered by the previous update.
        var next = Path.Combine(Dir(session), "Next.fbx");
        Edit(edited, next, scene => {
            var parent = Nodes(scene.GetRootNode()).Single(n => n.GetName().Contains(id));
            var added = FbxNode.Create(scene, "Grandchild__KCB_" + Guid.NewGuid().ToString("N"));
            added.SetNodeAttribute(parent.GetMesh());
            for (int i = 0; i < parent.GetMaterialCount(); i++) added.AddMaterial(parent.GetMaterial(i));
            parent.AddChild(added);
        });
        File.Copy(next, edited, true);
        Apply(session);
        Check(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<MeshFilter>().Length == 4, "Repeated child addition failed");
    }
    public static void Run()
    {
        Results.Clear();
        Folder = "Assets/BridgeRegression_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(Folder));
        int failed = 0;
        foreach (var test in new Dictionary<string, Action> {
            {"FBX edit -> undo -> repeat", () => UndoCase(false)},
            {"Primitive edit -> undo -> repeat", () => UndoCase(true)},
            {"Standalone and subsequent child", () => AdditionCase(false, false)},
            {"Child in multi-model prefab and subsequent child", () => AdditionCase(true, false)},
            {"Direct MeshFilter child and subsequent child", () => AdditionCase(true, true)},
            {"Separate empty slots survive resend", () => EmptySlots(false)},
            {"Renderer slots shorter than submeshes", () => EmptySlots(true)},
            {"Unassigned and out-of-range returned material IDs", MissingReturnedMaterials},
            {"Heartbeat fresh, stale, malformed and missing", ConnectionStates},
            {"Polling malformed session reports error", PollError},
            {"Standalone package and generated connector", ConnectionSetup},
            {"English and Russian language switching", LanguageSwitch},
            {"Multi-material drop queue and duplicate filtering", MaterialDropQueue},
            {"Repeated session writes on current storage", SessionWrites},
            {"Update preview targets and source directory", UpdateTargets},
            {"Backup size counts snapshots and originals only", BackupSizes} })
        {
            try { test.Value(); Results.Add("PASS " + test.Key); }
            catch (Exception e) { failed++; Results.Add("FAIL " + test.Key + "\n" + e); }
        }
        File.WriteAllLines("regression-results.txt", Results);
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    static void ConnectionSetup()
    {
        var type = typeof(MaxBridgeWindow);
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.Assembly);
        Check(package != null && package.name == "com.da1sycr.unity3dsmaxbridge", "Standalone package identity is wrong");
        Check(type.Assembly.GetName().Name == "Unity3dsMaxBridge.Editor", "Assembly identity is wrong");
        var script = (string)Call(type, null, "WriteConnectionScript");
        Check(File.Exists(script), "Connector was not generated");
        var text = File.ReadAllText(script);
        Check(!text.Contains("{{BRIDGE_DIRECTORY}}"), "Connector still contains an unresolved path");
        Check(text.Contains("macroScript Unity3dsMaxBridge"), "Persistent Max action is missing");
        Check(File.ReadAllBytes(script).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "Unicode connector is missing UTF-8 BOM");
        Check(script.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity3dsMaxBridge"), StringComparison.OrdinalIgnoreCase), "Connector is not stored in the current user's folder");
    }

    static void UpdateTargets()
    {
        var path = Fixture("PreviewTargets", multiple: true);
        var session = Send(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        var lines = File.ReadAllLines(Path.Combine(Dir(session), "UpdateTargets.txt"));
        Check(lines.First() == "UMB_UPDATE_TARGETS_1" && lines.Last() == "UMB_END", "Preview registry is incomplete");
        var files = ((IEnumerable)Get(session, "fbxFiles")).Cast<object>().ToArray();
        Check(lines.Contains("new-directory|" + Path.GetDirectoryName((string)Get(files[0], "path")).Replace('\\', '/')), "New mesh directory does not match Unity apply");
        foreach (var binding in ((IEnumerable)Get(session, "fbxBindings")).Cast<object>())
        {
            var file = files.Single(f => (string)Get(f, "guid") == (string)Get(binding, "fileGuid"));
            Check(lines.Contains("source|" + Get(binding, "nodeId") + "|" + Get(file, "path")), "Source mesh target is missing");
        }
    }

    static void BackupSizes()
    {
        var root = Path.GetFullPath("BackupSizeFixtures_" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "session-a");
        Directory.CreateDirectory(Path.Combine(first, "Backups", "revision"));
        Directory.CreateDirectory(Path.Combine(first, "InitialSources", "model"));
        File.WriteAllBytes(Path.Combine(first, "Backups", "revision", "original.fbx"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(first, "InitialSources", "model", "source.fbx"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(first, "ToMax.fbx"), new byte[8192]);
        File.WriteAllText(Path.Combine(first, "session.json"), "{\"sourcePath\":\"Assets/Модели/Дом.prefab\"}");
        var second = Path.Combine(root, "session-b", "Backups");
        Directory.CreateDirectory(second);
        File.WriteAllBytes(Path.Combine(second, "original.prefab"), new byte[512]);
        var type = typeof(MaxBridgeWindow).Assembly.GetType("Unity3dsMaxBridge.Editor.BridgeBackupStorage");
        var report = Call(type, null, "Scan", root);
        Check((long)Get(report, "totalBytes") == 3584L && (int)Get(report, "totalFiles") == 3, "Backup usage includes exchange files or misses nested backups");
        var sessions = ((IEnumerable)Get(report, "sessions")).Cast<object>().ToArray();
        Check(sessions.Length == 2 && sessions.Any(s => (string)Get(s, "name") == "Дом"), "Backup sessions or Unicode prefab names are missing");
        var empty = Call(type, null, "Scan", Path.Combine(root, "missing"));
        Check((long)Get(empty, "totalBytes") == 0L && (int)Get(empty, "unreadable") == 0, "Missing storage is not handled as empty");
    }

    static void LanguageSwitch()
    {
        var type = typeof(MaxBridgeWindow).Assembly.GetType("Unity3dsMaxBridge.Editor.BridgeLocalization");
        var language = type.GetProperty("IsRussian", Flags);
        var original = language.GetValue(null);
        try
        {
            language.SetValue(null, false);
            Check((string)Call(type, null, "Text", "Подключено") == "Connected", "English labels are not selected");
            Check((string)Call(type, null, "Text", "Не удалось сохранить prefab.") == "Could not save the prefab.", "English errors are not selected");
            language.SetValue(null, true);
            Check((string)Call(type, null, "Text", "Подключено") == "Подключено", "Russian labels are not selected");
        }
        finally { language.SetValue(null, original); }
    }

    static void MaterialDropQueue()
    {
        var window = ScriptableObject.CreateInstance<MaxBridgeWindow>();
        var a = new Material(Shader.Find("Standard"));
        var b = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(a, Folder + "/PaletteA.mat");
        AssetDatabase.CreateAsset(b, Folder + "/PaletteB.mat");
        try
        {
            typeof(MaxBridgeWindow).GetField("session", Flags).SetValue(window, null);
            Call(typeof(MaxBridgeWindow), window, "AddMaterials", new object[] { new Object[] { a, b, a, null } });
            Check(((ICollection)Get(window, "extraMaterials")).Count == 2, "Drop did not filter duplicates");
            Check((bool)Get(window, "materialsPending"), "Materials were lost without an active Max session");
            Call(typeof(MaxBridgeWindow), window, "AddMaterials", new object[] { new Object[] { a } });
            Check(((ICollection)Get(window, "extraMaterials")).Count == 2, "Repeated drop duplicated a material");
        }
        finally { Object.DestroyImmediate(window); }
    }

    static void SessionWrites()
    {
        var type = typeof(MaxBridgeWindow).Assembly.GetType("Unity3dsMaxBridge.Editor.BridgeSession");
        var session = Activator.CreateInstance(type);
        var directory = Path.GetFullPath("SessionIOFixtures");
        Directory.CreateDirectory(directory);
        type.GetField("directory").SetValue(session, directory);
        type.GetField("id").SetValue(session, "session-write-check");
        Call(typeof(MaxBridgeWindow), null, "Save", session);
        type.GetField("syncMessage").SetValue(session, "second write");
        Call(typeof(MaxBridgeWindow), null, "Save", session);
        Check(File.ReadAllText(Path.Combine(directory, "session.json")).Contains("second write"), "Repeated session save failed");
        Check(!File.Exists(Path.Combine(directory, "session.json.tmp")), "Session temporary file was not cleaned up");
    }

    static void EmptySlots(bool shortRenderer)
    {
        var path = Fixture(shortRenderer ? "ShortSlots" : "EmptySlots", loose: true);
        var contents = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var filter = contents.GetComponentInChildren<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh);
            var triangles = mesh.triangles;
            mesh.subMeshCount = 3;
            for (int i = 0; i < 3; i++) mesh.SetTriangles(triangles.Skip(i * 12).Take(12).ToArray(), i);
            AssetDatabase.CreateAsset(mesh, Folder + "/" + (shortRenderer ? "short" : "empty") + ".asset");
            filter.sharedMesh = mesh;
            var renderer = filter.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = shortRenderer ? new[] {renderer.sharedMaterial} : new[] {renderer.sharedMaterial, null, null};
            PrefabUtility.SaveAsPrefabAsset(contents, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
        var session = Send(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        var baseline = Path.Combine(Dir(session), "MaxBaseline.fbx");
        Edit(Path.Combine(Dir(session), "ToMax.fbx"), baseline, _ => {});
        Edit(baseline, Path.Combine(Dir(session), "Edited.fbx"), scene => {
            var mesh = Nodes(scene.GetRootNode()).First(n => n.GetMesh() != null).GetMesh();
            for (int i = 0; i < mesh.GetControlPointsCount(); i++) { var p = mesh.GetControlPointAt(i); p[0] *= 2; mesh.SetControlPointAt(p,i); }
        });
        Apply(session);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Check(prefab.GetComponentInChildren<MeshFilter>().sharedMesh.subMeshCount == 3, "Empty submeshes merged");
        Check(prefab.GetComponentInChildren<MeshRenderer>().sharedMaterials.Count(m => m == null) == 2, "Empty materials not preserved");
        var second = Send(prefab);
        Check(((IEnumerable)Get(second,"materials")).Cast<object>().Count(m => (bool)Get(m,"emptySlot")) == 2, "Empty slots merged on resend");
    }

    static void MissingReturnedMaterials()
    {
        var path = Fixture("MissingReturned", loose: true);
        var session = Send(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        var baseline = Path.Combine(Dir(session), "MaxBaseline.fbx");
        Edit(Path.Combine(Dir(session), "ToMax.fbx"), baseline, _ => {});
        Edit(baseline, Path.Combine(Dir(session), "Edited.fbx"), scene => {
            var node = Nodes(scene.GetRootNode()).First(n => n.GetMesh() != null);
            while (node.GetMaterialCount() > 0) node.DisconnectSrcObject(node.GetMaterial(0));
            var mesh = node.GetMesh();
            var layer = FbxLayerElementMaterial.Create(mesh,"Materials");
            layer.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygon);
            layer.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
            for (int i = 0; i < mesh.GetPolygonCount(); i++) layer.GetIndexArray().Add(i % 2 == 0 ? -1 : 5);
            mesh.GetLayer(0).SetMaterials(layer);
        });
        Apply(session);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Check(prefab.GetComponentInChildren<MeshFilter>().sharedMesh.subMeshCount == 2, "Unassigned IDs merged");
        Check(prefab.GetComponentInChildren<MeshRenderer>().sharedMaterials.All(m => m == null), "Unassigned IDs acquired material");
    }

    static void ConnectionStates()
    {
        var folder = Path.GetFullPath("HeartbeatFixtures"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder,"max.heartbeat");
        var type = typeof(MaxBridgeWindow).Assembly.GetType("Unity3dsMaxBridge.Editor.BridgeConnectionStatus");
        File.WriteAllText(file,"KCB_HEARTBEAT_1\nsession-a\nerror\nExport failed\nfalse\nsession-a\nKCB_END\n");
        var state = Call(type,null,"Read",folder);
        Check((bool)Get(state,"responding") && (string)Get(state,"state") == "error" && (string)Get(state,"sessionId") == "session-a", "Fresh heartbeat not parsed");
        File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddSeconds(-30));
        Check(!(bool)Get(Call(type,null,"Read",folder),"responding"), "Stale heartbeat considered live");
        File.WriteAllText(file,"partial");
        Check(!(bool)Get(Call(type,null,"Read",folder),"responding"), "Malformed heartbeat considered live");
        File.Delete(file);
        Check(!(bool)Get(Call(type,null,"Read",folder),"responding"), "Missing heartbeat considered live");
    }

    static void PollError()
    {
        var type = typeof(MaxBridgeWindow).Assembly.GetType("Unity3dsMaxBridge.Editor.MaxBridgeAutoSync");
        var preference = (string)typeof(MaxBridgeWindow).GetProperty("SessionPreference",Flags).GetValue(null);
        var original = EditorPrefs.GetString(preference,"");
        var path = Path.GetFullPath("bad-session.json"); File.WriteAllText(path,"not json");
        try
        {
            EditorPrefs.SetString(preference,path);
            type.GetField("nextPoll",Flags).SetValue(null,0d);
            Call(type,null,"Poll");
            Check(!string.IsNullOrEmpty((string)type.GetProperty("LastError",Flags).GetValue(null)), "Polling error not exposed");
            Check(!(bool)type.GetField("busy",Flags).GetValue(null), "Polling stuck busy");
        }
        finally { EditorPrefs.SetString(preference,original); File.Delete(path); }
    }

    public static void PrepareMax()
    {
        Folder = "Assets/MaxRoundtrip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(Folder));
        var path = Fixture("MaxUndo");
        var session = Send(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        File.WriteAllText("max-session.json", JsonUtility.ToJson(session, true));
        EditorApplication.Exit(0);
    }
    public static void ApplyMaxEdit() => ApplyMax(false);
    public static void ApplyMaxUndo() => ApplyMax(true);
    static void ApplyMax(bool undo)
    {
        try
        {
            var type = typeof(MaxBridgeWindow).Assembly.GetType("Unity3dsMaxBridge.Editor.BridgeSession");
            var session = JsonUtility.FromJson(File.ReadAllText("max-session.json"), type);
            Apply(session);
            var vertices = Vertices((string)Get(session,"sourcePath"));
            var width = vertices.Max(v => v.x) - vertices.Min(v => v.x);
            Check(Mathf.Abs(width - (undo ? 1f : 2f)) < .001f, "Unexpected width: " + width);
            File.WriteAllText("max-session.json", JsonUtility.ToJson(session, true));
            File.AppendAllText("max-results.txt", "PASS actual Max " + (undo ? "Undo" : "edit") + ": width=" + width + "\n");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { File.AppendAllText("max-results.txt", "FAIL " + e + "\n"); EditorApplication.Exit(1); }
    }
}
