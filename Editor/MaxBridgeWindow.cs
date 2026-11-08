using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity3dsMaxBridge.Editor
{
    // Exchange data and backups live outside the project. Meshes stay in their
    // original FBX assets so existing prefab and scene references remain valid.
    public sealed partial class MaxBridgeWindow : EditorWindow
    {
        internal const string NodeTag = "__KCB_";
        internal const string MaterialTag = "__KCM_";
        internal static string BridgeDirectory
        {
            get
            {
                using (var hash = System.Security.Cryptography.SHA256.Create())
                {
                    var key = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Application.dataPath))).Replace("-", "").Substring(0, 16);
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity3dsMaxBridge", key);
                }
            }
        }
        internal static string SessionPreference => "Unity3dsMaxBridge.Session." + Application.dataPath;
        [SerializeField] private GameObject source;
        [SerializeField] private bool autoSync = true;
        [SerializeField] private bool meshColliders = true;
        [SerializeField] private List<Material> extraMaterials = new List<Material>();
        [SerializeField] private bool materialsPending;
        [SerializeField] private bool materialSendFailed;
        private BridgeSession session;
        private string message;
        private Vector2 scroll;

        [MenuItem("Tools/Unity3dsMaxBridge/Open")]
        public static void Open()
        {
            var window = GetWindow<MaxBridgeWindow>("3ds Max Bridge");
            window.minSize = new Vector2(360, 320);
            var selected = Selection.activeGameObject;
            if (selected != null) window.source = selected;
        }

        private void OnEnable()
        {
            var file = EditorPrefs.GetString(SessionPreference, "");
            try { if (File.Exists(file)) session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(file)); }
            catch (Exception e) { message = BridgeLocalization.Text("Не удалось прочитать сессию: ") + e.Message; messageType = MessageType.Error; }
            if (session != null) { autoSync = session.autoSync; meshColliders = session.meshColliders; }
        }

        private void OnGUI() => DrawBridgeGUI();

        private void Run(Action action)
        {
            try { messageType = MessageType.Info; action(); }
            catch (Exception e) { messageType = MessageType.Error; message = e.Message; BridgeLog.Write(session, "window.error", e.Message, e); Debug.LogException(e); }
        }

        private static string DependencyHash(string path) => AssetDatabase.GetAssetDependencyHash(path).ToString();

        internal static string ResolveSourcePath(GameObject candidate)
        {
            if (candidate == null) return "";
            string path;
            if (EditorUtility.IsPersistent(candidate))
            {
                // A selected child can belong to a nested model. Resolve the
                // containing prefab asset rather than that child's FBX source.
                path = AssetDatabase.GetAssetPath(candidate.transform.root.gameObject);
            }
            else
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && candidate.scene == stage.scene)
                    path = stage.assetPath;
                else
                {
                    var root = PrefabUtility.GetOutermostPrefabInstanceRoot(candidate);
                    path = root == null ? "" : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                }
            }
            return !string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? path : "";
        }

        private static string SourceSelectionError(GameObject candidate)
        {
            if (candidate == null) return BridgeLocalization.Text("Выбери prefab в Project или его экземпляр в сцене.");
            var path = AssetDatabase.GetAssetPath(candidate);
            if (PrefabUtility.GetPrefabAssetType(candidate) == PrefabAssetType.Model ||
                (!string.IsNullOrEmpty(path) && !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                return BridgeLocalization.Text("Выбрана модель «") + candidate.name + BridgeLocalization.Text("». Выбери .prefab-сборку, которая использует эту модель, или экземпляр этой сборки в сцене.");
            return BridgeLocalization.Text("У объекта «") + candidate.name + BridgeLocalization.Text("» нет исходного .prefab. Сохрани его как prefab или выбери существующий prefab в Project.");
        }
        private static void RequireSavedPrefab(string path)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == path && stage.scene.isDirty)
                throw new InvalidOperationException(BridgeLocalization.Text("Сначала сохрани изменения prefab в Prefab Mode."));
        }
        private static void EnsureAssetFolder(string path)
        {
            var parts = path.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private void PrepareConnection()
        {
            var destination = WriteConnectionScript();
            EditorGUIUtility.systemCopyBuffer = destination;
            EditorUtility.RevealInFinder(destination);
            message = BridgeLocalization.Text("В Max: Scripting → Run Script → Connect_Max.ms. Путь скопирован в буфер обмена.");
        }

        internal static string WriteConnectionScript()
        {
            RequireWindowsEditor();
            Directory.CreateDirectory(BridgeDirectory);
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MaxBridgeWindow).Assembly);
            var script = package == null ? null : Path.Combine(package.resolvedPath, "Editor", "MaxBridgeConnection.txt");
            if (string.IsNullOrEmpty(script) || !File.Exists(script))
                script = AssetDatabase.FindAssets("MaxBridgeConnection").Select(AssetDatabase.GUIDToAssetPath)
                    .Single(p => p.EndsWith("MaxBridgeConnection.txt", StringComparison.Ordinal));
            var text = File.ReadAllText(script).Replace("{{BRIDGE_DIRECTORY}}", BridgeDirectory.Replace('\\', '/'));
            var destination = Path.Combine(BridgeDirectory, "Connect_Max.ms");
            File.WriteAllText(destination, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new System.Text.UTF8Encoding(true));
            return destination;
        }

        private static void RequireWindowsEditor()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                throw new PlatformNotSupportedException(BridgeLocalization.Text("Unity3dsMaxBridge поддерживает Unity Editor и 3ds Max на Windows."));
        }

        private void CreateIndependentCopy()
        {
            var path = ResolveSourcePath(source);
            RequireSavedPrefab(path);
            source = UniquePrefabCopy.Create(path);
            Selection.activeObject = source;
            EditorGUIUtility.PingObject(source);
            message = BridgeLocalization.Text("Копия создана. Отправь её в Max для новой сессии.");
        }

        private void SaveSession()
        {
            var path = Path.Combine(session.directory, "session.json");
            Save(session);
            EditorPrefs.SetString(SessionPreference, path);
        }

        internal void Send(GameObject prefab)
        {
            RequireWindowsEditor();
            var path = ResolveSourcePath(prefab);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException(SourceSelectionError(prefab));
            RequireSavedPrefab(path);
            session = new BridgeSession
            {
                id = Guid.NewGuid().ToString("N"), sourcePath = path,
                sourceHash = DependencyHash(path), sourceGuid = AssetDatabase.AssetPathToGUID(path), autoSync = autoSync, meshColliders = meshColliders
            };
            session.directory = Path.Combine(BridgeDirectory, session.id);
            Directory.CreateDirectory(session.directory);
            AddExtraMaterials();
            var contents = PrefabUtility.LoadPrefabContents(path);
            GameObject clone = null;
            GameObject transport = null;
            var proxies = new Dictionary<long, Material>();
            var exportMeshes = new List<Mesh>();
            try
            {
                if (contents.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
                    throw new InvalidOperationException(BridgeLocalization.Text("Эта версия bridge поддерживает только статические меши."));
                if (!contents.GetComponentsInChildren<MeshFilter>(true).Any(f => f.sharedMesh != null))
                    throw new InvalidOperationException(BridgeLocalization.Text("В prefab нет MeshFilter с геометрией."));
                clone = CloneGeometry(contents.transform, contents.transform, null, "", "", proxies, exportMeshes);
                clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                clone.transform.localScale = Vector3.one;
                // Unity renames an imported FBX's top-level node after the file.
                // Keep the identified prefab root one level below that wrapper.
                transport = new GameObject("KCB_Transport");
                clone.transform.SetParent(transport.transform, false);
                var options = new ExportModelOptions
                {
                    ExportFormat = ExportFormat.Binary, ModelAnimIncludeOption = Include.Model,
                    ObjectPosition = ObjectPosition.WorldAbsolute, KeepInstances = false,
                    EmbedTextures = true, ExportUnrendered = true, UseMayaCompatibleNames = false
                };
                var exported = ModelExporter.ExportObject(Path.Combine(session.directory, "ToMax.fbx"), transport, options);
                if (string.IsNullOrEmpty(exported)) throw new InvalidOperationException(BridgeLocalization.Text("Unity FBX Exporter не создал файл."));
                SourceFbxBridge.Capture(session, contents);
                BridgeLog.Write(session, "send.ready", "models=" + session.fbxFiles.Count + "; meshes=" + session.fbxBindings.Count);
                WriteMaterialPalette();
                SaveSession();
                var request = Path.Combine(BridgeDirectory, "open.request");
                var temporary = request + ".tmp";
                File.WriteAllText(temporary, session.directory.Replace('\\', '/'));
                if (File.Exists(request)) File.Delete(request);
                File.Move(temporary, request);
                message = BridgeLocalization.Text("Prefab подготовлен. Подключённый Max импортирует его. Если окно bridge ещё не запущено, сначала выполни шаг 1.");
            }
            finally
            {
                if (transport != null) DestroyImmediate(transport);
                else if (clone != null) DestroyImmediate(clone);
                foreach (var proxy in proxies.Values) DestroyImmediate(proxy);
                foreach (var mesh in exportMeshes) DestroyImmediate(mesh);
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private void AddExtraMaterials()
        {
            foreach (var material in extraMaterials.Where(m => m != null).Distinct())
            {
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId))
                    throw new InvalidOperationException(BridgeLocalization.Text("Сначала сохрани материал в Assets: ") + material.name);
                var existing = session.materials.FirstOrDefault(m => m.guid == guid && m.localId == localId);
                if (existing != null)
                {
                    existing.extra = true;
                    continue;
                }
                session.materials.Add(new BridgeMaterial { id = SourceFbxBridge.StableMaterialId(guid, localId), guid = guid, localId = localId, name = material.name, slot = -1, extra = true });
            }
        }

        private void WriteMaterialPalette()
        {
            var lines = new List<string>();
            foreach (var record in session.materials.Where(m => m.extra))
            {
                var material = SourceFbxBridge.ResolveMaterial(session, record.id);
                if (material == null) continue;
                var baseProperty = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                var colorProperty = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                var color = material.HasProperty(colorProperty) ? material.GetColor(colorProperty) : Color.white;
                var scale = material.HasProperty(baseProperty) ? material.GetTextureScale(baseProperty) : Vector2.one;
                var offset = material.HasProperty(baseProperty) ? material.GetTextureOffset(baseProperty) : Vector2.zero;
                string Number(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                lines.Add(string.Join("|", record.id, record.name.Replace("|", " ").Replace("\n", " ").Replace("\r", " "),
                    PaletteTexture(material, baseProperty, record.id + "_base"), Number(color.r), Number(color.g), Number(color.b),
                    Number(scale.x), Number(scale.y), Number(offset.x), Number(offset.y),
                    PaletteTexture(material, "_BumpMap", record.id + "_normal"), PaletteTexture(material, "_EmissionMap", record.id + "_emission")));
            }
            var path = Path.Combine(session.directory, "UnityMaterials.txt");
            File.WriteAllLines(path + ".tmp", lines);
            CommitExchangeFile(path + ".tmp", path);
            BridgeLog.Write(session, "materials.palette", "count=" + lines.Count + "; names=" + string.Join(", ", session.materials.Where(m => m.extra).Select(m => m.name)));
        }

        private string PaletteTexture(Material material, string property, string key)
        {
            if (!material.HasProperty(property)) return "-";
            var texture = material.GetTexture(property);
            if (texture == null) return "-";
            var assetPath = AssetDatabase.GetAssetPath(texture);
            var extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if (new[] { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".bmp", ".exr", ".hdr", ".dds" }.Contains(extension) && File.Exists(assetPath))
                return Path.GetFullPath(assetPath).Replace('\\', '/');
            // Procedural and non-bitmap source formats are exported for preview
            // outside Assets, without changing the Unity texture's import settings.
            var directory = Path.Combine(session.directory, "MaterialTextures");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, key + ".png");
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D readable = null;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                readable.Apply();
                File.WriteAllBytes(destination, readable.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (readable != null) DestroyImmediate(readable);
            }
            return destination.Replace('\\', '/');
        }

        [MenuItem("Tools/Unity3dsMaxBridge/Refresh material previews")]
        private static void RefreshMaterialPreviews()
        {
            var window = Resources.FindObjectsOfTypeAll<MaxBridgeWindow>().FirstOrDefault();
            var temporary = window == null;
            if (temporary) window = CreateInstance<MaxBridgeWindow>();
            try
            {
                window.session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(EditorPrefs.GetString(SessionPreference)));
                window.AddExtraMaterials();
                window.SaveSession();
                window.WriteMaterialPalette();
                File.WriteAllText(Path.Combine(window.session.directory, "materials.request"), Guid.NewGuid().ToString("N"));
            }
            finally { if (temporary) DestroyImmediate(window); }
        }

        [MenuItem("Tools/Unity3dsMaxBridge/Resend active prefab")]
        private static void ResendActivePrefab()
        {
            var path = EditorPrefs.GetString(SessionPreference, "");
            if (!File.Exists(path)) throw new InvalidOperationException(BridgeLocalization.Text("Нет активной сессии bridge."));
            var current = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(path));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(current.sourcePath);
            GetWindow<MaxBridgeWindow>().Run(() => GetWindow<MaxBridgeWindow>().Send(prefab));
        }

        private void SendMaterials()
        {
            // Read the latest revision; auto-sync may have advanced the session
            // while this EditorWindow was displaying the material picker.
            var path = EditorPrefs.GetString(SessionPreference, "");
            if (!File.Exists(path)) throw new InvalidOperationException(BridgeLocalization.Text("Сначала отправь prefab в Max."));
            session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(path));
            AddExtraMaterials();
            SaveSession();
            WriteMaterialPalette();
            File.WriteAllText(Path.Combine(session.directory, "materials.request"), Guid.NewGuid().ToString("N"));
            materialsPending = false;
            materialSendFailed = false;
            message = BridgeLocalization.Text("Материалы отправлены в Max.");
        }

        private void AddMaterials(Object[] objects)
        {
            var added = false;
            foreach (var material in objects.OfType<Material>())
            {
                if (!EditorUtility.IsPersistent(material) || extraMaterials.Contains(material)) continue;
                extraMaterials.Add(material);
                added = true;
            }
            if (!added && !materialSendFailed) return;
            materialSendFailed = false;
            materialsPending = true;
            TrySendPendingMaterials();
        }

        private void TrySendPendingMaterials()
        {
            if (!materialsPending || materialSendFailed || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            var current = BridgeConnectionStatus.Read(BridgeDirectory);
            if (session == null || !current.responding || current.sessionId != session.id ||
                current.state == "importing" || current.state == "exporting" || current.state == "error") return;
            // A failed write is reported once; adding a material or retrying manually can resend it.
            materialSendFailed = true;
            Run(SendMaterials);
        }

        private GameObject CloneGeometry(Transform original, Transform root, Transform parent, string path, string parentId, Dictionary<long, Material> proxies, List<Mesh> exportMeshes)
        {
            var id = Guid.NewGuid().ToString("N");
            var clone = new GameObject(original.name + NodeTag + id);
            clone.transform.SetParent(parent, false);
            // Max and Unity evaluate reflected/nonuniform parent scale differently
            // across rotated FBX nodes. Export a translation-only hierarchy and bake the
            // remaining affine transform into temporary geometry instead.
            clone.transform.position = root.worldToLocalMatrix.MultiplyPoint3x4(original.position);
            clone.transform.rotation = Quaternion.identity;
            clone.transform.localScale = Vector3.one;
            var filter = original.GetComponent<MeshFilter>();
            var renderer = original.GetComponent<MeshRenderer>();
            session.nodes.Add(new BridgeNode { id = id, path = path, parentId = parentId, hasMesh = filter != null && filter.sharedMesh != null, name = original.name });
            if (filter != null && filter.sharedMesh != null)
            {
                if (renderer == null) throw new InvalidOperationException(BridgeLocalization.Text("MeshFilter без MeshRenderer пока не поддерживается: ") + original.name);
                if (filter.sharedMesh.blendShapeCount != 0)
                    throw new InvalidOperationException(BridgeLocalization.Text("Blend shapes пока не поддерживаются: ") + original.name);
                var mesh = Instantiate(filter.sharedMesh);
                exportMeshes.Add(mesh);
                mesh.name = filter.sharedMesh.name;
                var bake = clone.transform.worldToLocalMatrix * root.worldToLocalMatrix * original.localToWorldMatrix;
                mesh.vertices = mesh.vertices.Select(v => bake.MultiplyPoint3x4(v)).ToArray();
                var normalFrame = bake.inverse.transpose;
                mesh.normals = mesh.normals.Select(n => normalFrame.MultiplyVector(n).normalized).ToArray();
                var mirrored = bake.determinant < 0;
                mesh.tangents = mesh.tangents.Select(t =>
                {
                    var direction = bake.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                    return new Vector4(direction.x, direction.y, direction.z, mirrored ? -t.w : t.w);
                }).ToArray();
                if (mirrored)
                    for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        var triangles = mesh.GetTriangles(submesh);
                        for (var i = 0; i < triangles.Length; i += 3)
                        { var index = triangles[i + 1]; triangles[i + 1] = triangles[i + 2]; triangles[i + 2] = index; }
                        mesh.SetTriangles(triangles, submesh, false);
                    }
                mesh.RecalculateBounds();
                clone.AddComponent<MeshFilter>().sharedMesh = mesh;
                var copyRenderer = clone.AddComponent<MeshRenderer>();
                var sourceMaterials = MaterialSlots(renderer, filter.sharedMesh);
                copyRenderer.sharedMaterials = sourceMaterials.Select((m, slot) => ProxyMaterial(m, slot, proxies)).ToArray();
            }
            for (var i = 0; i < original.childCount; i++)
                CloneGeometry(original.GetChild(i), root, clone.transform, path.Length == 0 ? i.ToString() : path + "/" + i, id, proxies, exportMeshes);
            return clone;
        }

        internal static Material[] MaterialSlots(MeshRenderer renderer, Mesh mesh)
        {
            var materials = renderer.sharedMaterials;
            return Enumerable.Range(0, Math.Max(1, Math.Max(materials.Length, mesh.subMeshCount)))
                .Select(i => i < materials.Length ? materials[i] : null).ToArray();
        }

        private Material ProxyMaterial(Material original, int slot, Dictionary<long, Material> proxies)
        {
            var key = original == null ? long.MinValue + slot : (long)original.GetInstanceID();
            if (proxies.TryGetValue(key, out var existing)) return existing;
            string guid = "";
            long localId = 0;
            if (original != null && !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original, out guid, out localId))
                throw new InvalidOperationException(BridgeLocalization.Text("Сначала сохрани материал в Assets: ") + original.name);
            var name = original == null ? "MissingMaterial" : original.name;
            var record = session.materials.FirstOrDefault(m => m.guid == guid && m.localId == localId && (original != null || m.slot == slot));
            if (record == null)
            {
                record = new BridgeMaterial { id = SourceFbxBridge.StableMaterialId(original == null ? "empty-slot:" + slot : guid, localId), guid = guid, localId = localId, name = name, slot = slot, emptySlot = original == null };
                session.materials.Add(record);
            }
            var id = record.id;
            var shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException(BridgeLocalization.Text("Не найден Standard shader для предпросмотра материалов в Max."));
            var proxy = new Material(shader) { name = name + MaterialTag + id };
            proxies.Add(key, proxy);
            if (original == null) return proxy;
            var baseProperty = original.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            if (original.HasProperty(baseProperty))
            {
                proxy.SetTexture("_MainTex", original.GetTexture(baseProperty));
                proxy.SetTextureScale("_MainTex", original.GetTextureScale(baseProperty));
                proxy.SetTextureOffset("_MainTex", original.GetTextureOffset(baseProperty));
            }
            var colorProperty = original.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
            if (original.HasProperty(colorProperty)) proxy.color = original.GetColor(colorProperty);
            foreach (var property in new[] { "_BumpMap", "_EmissionMap", "_MetallicGlossMap" })
                if (original.HasProperty(property)) proxy.SetTexture(property, original.GetTexture(property));
            foreach (var property in new[] { "_Metallic", "_Glossiness", "_BumpScale" })
                if (original.HasProperty(property)) proxy.SetFloat(property, original.GetFloat(property));
            if (original.HasProperty("_Smoothness")) proxy.SetFloat("_Glossiness", original.GetFloat("_Smoothness"));
            if (original.HasProperty("_EmissionColor")) proxy.SetColor("_EmissionColor", original.GetColor("_EmissionColor"));
            if (proxy.GetTexture("_BumpMap") != null) proxy.EnableKeyword("_NORMALMAP");
            proxy.EnableKeyword("_EMISSION");
            return proxy;
        }

        internal static string Token(string name, string marker)
        {
            var start = name.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0 || name.Length < start + marker.Length + 32) return null;
            var value = name.Substring(start + marker.Length, 32);
            return Guid.TryParseExact(value, "N", out _) ? value : null;
        }

        internal static Transform FindIndexed(Transform root, string path)
        {
            var current = root;
            if (string.IsNullOrEmpty(path)) return current;
            foreach (var segment in path.Split('/'))
            {
                if (!int.TryParse(segment, out var index) || index < 0 || index >= current.childCount)
                    throw new InvalidOperationException(BridgeLocalization.Text("Структура prefab изменилась. Отправь его в Max заново."));
                current = current.GetChild(index);
            }
            return current;
        }

        internal static void Save(BridgeSession value)
        {
            var path = Path.Combine(value.directory, "session.json");
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(value, true));
                CommitExchangeFile(temporary, path);
                WriteUpdateTargets(value);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string destination, int flags);

        private static void CommitExchangeFile(string temporary, string destination)
        {
            // Mono's File.Replace can reject encrypted exchange files on Windows.
            // Prefer a rename; allow Windows to copy when storage redirects across volumes.
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                if (!MoveFileEx(temporary, destination, 0x1 | 0x2 | 0x8))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            else if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }

        internal void ReloadSession()
        {
            var path = EditorPrefs.GetString(SessionPreference, "");
            if (File.Exists(path)) session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(path));
            if (session != null) { autoSync = session.autoSync; meshColliders = session.meshColliders; }
            Repaint();
        }

        internal void ApplyReturn()
        {
            if (session == null) throw new InvalidOperationException(BridgeLocalization.Text("Сначала отправь prefab в Max."));
            RequireSavedPrefab(session.sourcePath);
            SourceFbxBridge.Apply(session);
            session.syncMessage = BridgeLocalization.Text("Обновлены исходные FBX. Unity переимпортировал меши; ссылки prefab сохранены.");
            session.lastRevision = File.Exists(Path.Combine(session.directory, "Edited.ready")) ? File.ReadAllText(Path.Combine(session.directory, "Edited.ready")) : "";
            SaveSession();
            message = session.syncMessage;
        }

        private void RequestUpdate()
        {
            if (session == null) throw new InvalidOperationException(BridgeLocalization.Text("Сначала отправь prefab в Max."));
            session.syncState = "requested";
            session.syncMessage = BridgeLocalization.Text("Запрос отправлен. Ожидаем экспорт из 3ds Max.");
            session.syncUtc = DateTime.UtcNow.ToString("O");
            SaveSession();
            File.WriteAllText(Path.Combine(session.directory, "apply.request"), "");
            File.WriteAllText(Path.Combine(session.directory, "sync.request"), "");
            message = BridgeLocalization.Text("Запрошено обновление из Max.");
        }
    }

    [Serializable] internal sealed class BridgeSession
    {
        public string id, sourcePath, sourceHash, sourceGuid, directory;
        // Retained for sessions created by the first version; no new copies are made.
        public string outputPath = "", outputHash = "";
        public bool autoSync = true, meshColliders = true;
        public string prefabFileHash, lastRevision, lastAttemptedRevision, syncMessage, syncState, syncUtc, lastApplySummary;
        public List<SourceFbxFile> fbxFiles = new List<SourceFbxFile>();
        public List<SourceFbxBinding> fbxBindings = new List<SourceFbxBinding>();
        public List<BridgeLooseMesh> looseMeshes = new List<BridgeLooseMesh>();
        public List<BridgeNode> nodes = new List<BridgeNode>();
        public List<BridgeMaterial> materials = new List<BridgeMaterial>();
    }
    [Serializable] internal sealed class BridgeNode { public string id, path, parentId, name; public bool hasMesh, added = false; }
    [Serializable] internal sealed class BridgeMaterial { public string id, guid, name; public long localId; public int slot = 0; public bool extra, emptySlot; }
}
