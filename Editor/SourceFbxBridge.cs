using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity3dsMaxBridge.Editor
{
    [Serializable] internal sealed class SourceFbxFile
    {
        public string guid, path, expectedHash, expectedMetaHash;
        public Matrix4x4 nativeToSdk;
        public List<SourceIdentity> identities = new List<SourceIdentity>();
    }
    [Serializable] internal sealed class SourceIdentity { public long id; public string type, name, materialGuid; public long materialLocalId; public bool emptySlot; }
    [Serializable] internal sealed class SourceMaterialSlot { public int index; public string materialId; public string originalMaterialId; }
    [Serializable] internal sealed class SourceFbxBinding
    {
        public string nodeId, fileGuid, modelPath, instancePath, baselineSignature, lastAppliedSignature, lastAppliedGeometrySignature;
        public long meshId;
        public bool bridgeCreated;
        public bool sourceAddition;
        public bool directReference;
        public Matrix4x4 prefabToMesh, meshToSdk;
        public List<string> materialIds = new List<string>();
        public List<SourceMaterialSlot> sourceMaterialSlots = new List<SourceMaterialSlot>();
    }

    internal static partial class SourceFbxBridge
    {
        internal static string Hash(string path)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }

        internal static string StableMaterialId(string guid, long localId)
        {
            using (var hash = MD5.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(guid + ":" + localId.ToString(CultureInfo.InvariantCulture)))).Replace("-", "").ToLowerInvariant();
        }

        private static void NormalizeReturnedMaterials(BridgeSession session, FbxScene scene)
        {
            // FBX exports unassigned Max IDs as separate untagged materials.
            // Give each slot a transport identity, without creating Unity materials.
            foreach (var node in Nodes(scene.GetRootNode()).Where(n => n.GetMesh() != null))
            {
                var mesh = node.GetMesh();
                var layer = mesh.GetLayerCount() == 0 ? null : mesh.GetLayer(0).GetMaterials();
                var maximum = 0;
                if (layer != null)
                {
                    var indices = layer.GetIndexArray();
                    for (var polygon = 0; polygon < mesh.GetPolygonCount(); polygon++)
                    {
                        var index = LayerIndex(layer, polygon, 0, 0);
                        if (index < 0 || index >= indices.GetCount())
                            throw new InvalidOperationException(BridgeLocalization.Text("Объект «") + node.GetName() + BridgeLocalization.Text("»: отсутствует material ID полигона ") + (polygon + 1) + BridgeLocalization.Text(". Назначь материал или ID в Max и повтори экспорт."));
                        maximum = Math.Max(maximum, indices.GetAt(index));
                    }
                    if (maximum > 65534) throw new InvalidOperationException(BridgeLocalization.Text("Объект «") + node.GetName() + BridgeLocalization.Text("»: material ID превышает 65535."));
                    // FBX uses -1 for an unassigned face. Preserve it as a distinct
                    // empty slot instead of treating it as material zero.
                    var missingSlot = Math.Max(maximum + 1, node.GetMaterialCount());
                    for (var i = 0; i < indices.GetCount(); i++)
                        if (indices.GetAt(i) < 0) { indices.SetAt(i, missingSlot); maximum = Math.Max(maximum, missingSlot); }
                }
                while (node.GetMaterialCount() <= maximum)
                    node.AddMaterial(FbxSurfacePhong.Create(scene, "Empty_ID_" + (node.GetMaterialCount() + 1)));
                for (var slot = 0; slot < node.GetMaterialCount(); slot++)
                {
                    var material = node.GetMaterial(slot);
                    if (material == null || MaxBridgeWindow.Token(material.GetName(), MaxBridgeWindow.MaterialTag) != null) continue;
                    var id = StableMaterialId("empty-slot:" + node.GetName(), slot);
                    var record = session.materials.FirstOrDefault(m => m.id == id);
                    if (record == null)
                    {
                        record = new BridgeMaterial { id = id, guid = "", name = "Empty_ID_" + (slot + 1), slot = slot, emptySlot = true };
                        session.materials.Add(record);
                        BridgeLog.Write(session, "material.empty-slot", node.GetName() + "; ID=" + (slot + 1) + "; Max material=" + material.GetName());
                    }
                    material.SetName(record.name + MaxBridgeWindow.MaterialTag + id);
                }
            }
            var materials = Nodes(scene.GetRootNode()).Where(n => n.GetMesh() != null)
                .SelectMany(n => Enumerable.Range(0, n.GetMaterialCount()).Select(n.GetMaterial)).Where(m => m != null).ToArray();
            var unknown = materials.Select(m => MaxBridgeWindow.Token(m.GetName(), MaxBridgeWindow.MaterialTag))
                .Where(id => id != null && !session.materials.Any(m => m.id == id)).Distinct().ToArray();
            if (unknown.Length == 0) return;
            var history = new Dictionary<string, BridgeMaterial>();
            foreach (var directory in Directory.EnumerateDirectories(Path.GetDirectoryName(session.directory)))
            {
                var path = Path.Combine(directory, "session.json");
                if (!File.Exists(path)) continue;
                var previous = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(path));
                if (previous?.materials == null) continue;
                foreach (var record in previous.materials.Where(m => unknown.Contains(m.id)))
                {
                    if (history.TryGetValue(record.id, out var duplicate) && (duplicate.guid != record.guid || duplicate.localId != record.localId))
                        throw new InvalidOperationException(BridgeLocalization.Text("Неоднозначная история материала bridge: ") + record.name);
                    history[record.id] = record;
                }
            }
            foreach (var id in unknown)
            {
                if (!history.TryGetValue(id, out var original) || (string.IsNullOrEmpty(original.guid) && !original.emptySlot))
                    throw new InvalidOperationException(BridgeLocalization.Text("Не найден материал из предыдущей сессии Max: ") + materials.First(m => MaxBridgeWindow.Token(m.GetName(), MaxBridgeWindow.MaterialTag) == id).GetName() + BridgeLocalization.Text(". Отправь этот материал из Unity в текущую сессию."));
                if (original.emptySlot)
                {
                    session.materials.Add(new BridgeMaterial { id = id, guid = "", name = original.name, slot = original.slot, emptySlot = true });
                    continue;
                }
                var assetPath = AssetDatabase.GUIDToAssetPath(original.guid);
                if (string.IsNullOrEmpty(assetPath) || !AssetDatabase.LoadAllAssetsAtPath(assetPath).Any(a => a is Material && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string _, out long localId) && localId == original.localId))
                    throw new InvalidOperationException(BridgeLocalization.Text("Исходный материал Unity больше не существует: ") + original.name);
                var current = session.materials.FirstOrDefault(m => m.guid == original.guid && m.localId == original.localId);
                if (current == null)
                {
                    current = new BridgeMaterial { id = StableMaterialId(original.guid, original.localId), guid = original.guid, localId = original.localId, name = original.name, slot = -1 };
                    session.materials.Add(current);
                }
                foreach (var material in materials.Where(m => MaxBridgeWindow.Token(m.GetName(), MaxBridgeWindow.MaterialTag) == id))
                    material.SetName(current.name + MaxBridgeWindow.MaterialTag + current.id);
                BridgeLog.Write(session, "material.session-alias", original.name + "; " + id + " -> " + current.id + "; guid=" + current.guid);
            }
        }

        private sealed class Document : IDisposable
        {
            public readonly FbxManager manager;
            public readonly FbxScene scene;
            private readonly FbxAxisSystem axes;
            private readonly FbxSystemUnit units;
            public Document()
            {
                manager = FbxManager.Create();
                manager.SetIOSettings(FbxIOSettings.Create(manager, Globals.IOSROOT));
                scene = FbxScene.Create(manager, "BridgeMeshes");
                scene.GetGlobalSettings().SetAxisSystem(FbxAxisSystem.DirectX);
                scene.GetGlobalSettings().SetSystemUnit(FbxSystemUnit.m);
                axes = new FbxAxisSystem(FbxAxisSystem.DirectX);
                units = new FbxSystemUnit(100, 1);
            }
            public Document(string path)
            {
                manager = FbxManager.Create();
                manager.SetIOSettings(FbxIOSettings.Create(manager, Globals.IOSROOT));
                scene = FbxScene.Create(manager, "Bridge");
                using (var importer = FbxImporter.Create(manager, ""))
                {
                    if (!importer.Initialize(path, -1, manager.GetIOSettings()) || !importer.Import(scene))
                    { manager.Destroy(); throw new InvalidOperationException(BridgeLocalization.Text("Не удалось прочитать FBX: ") + path); }
                }
                axes = new FbxAxisSystem(scene.GetGlobalSettings().GetAxisSystem());
                var unit = scene.GetGlobalSettings().GetSystemUnit();
                units = new FbxSystemUnit(unit.GetScaleFactor(), unit.GetMultiplier());
                FbxAxisSystem.DirectX.DeepConvertScene(scene);
                FbxSystemUnit.m.ConvertScene(scene);
            }
            public void NormalizeAddedRoot(FbxNode node)
            {
                // Unit conversion scales scene roots on save. A newly created root
                // must carry the same inverse conversion as the imported roots.
                // Geometry is baked against the resulting transform by the caller.
                if (node.GetParent() == scene.GetRootNode())
                {
                    var scale = units.GetScaleFactor() / FbxSystemUnit.m.GetScaleFactor();
                    node.LclScaling.Set(new FbxDouble3(scale, scale, scale));
                }
            }
            public void Save(string path)
            {
                axes.DeepConvertScene(scene);
                units.ConvertScene(scene);
                using (var exporter = FbxExporter.Create(manager, ""))
                    if (!exporter.Initialize(path, -1, manager.GetIOSettings()) || !exporter.Export(scene))
                        throw new InvalidOperationException(BridgeLocalization.Text("Не удалось записать FBX: ") + path);
            }
            public void Dispose() { axes.Dispose(); units.Dispose(); manager.Destroy(); }
        }

        private static IEnumerable<FbxNode> Nodes(FbxNode root)
        {
            for (var i = 0; i < root.GetChildCount(); i++)
            {
                var child = root.GetChild(i);
                yield return child;
                foreach (var descendant in Nodes(child)) yield return descendant;
            }
        }
        private static string NodePath(FbxNode node)
        {
            var segments = new List<string>();
            while (node.GetParent() != null) { segments.Add(node.GetName()); node = node.GetParent(); }
            segments.Reverse();
            return string.Join("/", segments);
        }
        private static FbxNode Find(FbxScene scene, string path)
        {
            var matches = Nodes(scene.GetRootNode()).Where(n => NodePath(n) == path).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException(BridgeLocalization.Text("Неоднозначный узел исходного FBX: ") + path);
            return matches[0];
        }
        private static FbxNode MatchNative(FbxScene scene, MeshFilter filter)
        {
            var meshNodes = Nodes(scene.GetRootNode()).Where(n => n.GetMesh() != null).ToArray();
            var named = meshNodes.Where(n => n.GetName() == filter.name).ToArray();
            if (named.Length == 1) return named[0];
            var byMesh = meshNodes.Where(n => n.GetMesh().GetName() == filter.sharedMesh.name).ToArray();
            if (byMesh.Length == 1) return byMesh[0];
            if (meshNodes.Length == 1) return meshNodes[0];
            throw new InvalidOperationException(BridgeLocalization.Text("Не удалось однозначно сопоставить меш с узлом FBX: ") + filter.name);
        }
        private static Vector3 Vec(FbxVector4 v) => new Vector3((float)v[0], (float)v[1], (float)v[2]);
        private static Matrix4x4 Matrix(FbxAMatrix matrix)
        {
            var origin = Vec(matrix.MultT(new FbxVector4(0, 0, 0, 1)));
            var result = Matrix4x4.identity;
            result.SetColumn(0, (Vector4)(Vec(matrix.MultT(new FbxVector4(1, 0, 0, 1))) - origin));
            result.SetColumn(1, (Vector4)(Vec(matrix.MultT(new FbxVector4(0, 1, 0, 1))) - origin));
            result.SetColumn(2, (Vector4)(Vec(matrix.MultT(new FbxVector4(0, 0, 1, 1))) - origin));
            result.SetColumn(3, new Vector4(origin.x, origin.y, origin.z, 1));
            return result;
        }
        private static Matrix4x4 GeometryWorld(FbxNode node)
        {
            using (var geometric = new FbxAMatrix())
            {
                geometric.SetT(node.GetGeometricTranslation(FbxNode.EPivotSet.eSourcePivot));
                geometric.SetR(node.GetGeometricRotation(FbxNode.EPivotSet.eSourcePivot));
                geometric.SetS(node.GetGeometricScaling(FbxNode.EPivotSet.eSourcePivot));
                return Matrix(node.EvaluateGlobalTransform()) * Matrix(geometric);
            }
        }
        private static string Id(Material material, BridgeSession session, int slot)
        {
            string guid = ""; long id = 0;
            if (material != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out guid, out id);
            var record = session.materials.Find(m => m.guid == guid && m.localId == id && m.slot == slot)
                ?? session.materials.Find(m => m.guid == guid && m.localId == id);
            if (record == null) throw new InvalidOperationException(BridgeLocalization.Text("Не найден материал сессии."));
            return record.id;
        }
        private static List<SourceIdentity> Identities(string path)
        {
            var result = new List<SourceIdentity>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long id))
                    result.Add(new SourceIdentity { id = id, type = asset.GetType().FullName, name = asset.name });
            return result;
        }

        private static Matrix4x4 MatchAxes(FbxScene scene, GameObject native, string path)
        {
            var pairs = native.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null)
                .Select(f => new { filter = f, sdk = MatchNative(scene, f) }).ToArray();
            var scale = ((ModelImporter)AssetImporter.GetAtPath(path)).globalScale;
            var candidates = new List<Matrix4x4> { Matrix4x4.Scale(Vector3.one / scale), Matrix4x4.Rotate(Quaternion.Euler(0, 180, 0)) * Matrix4x4.Scale(Vector3.one / scale) };
            // FBX exporter/importer combinations may preserve a root rotation or
            // flip a handedness axis. Test every signed axis permutation against
            // actual vertices rather than assuming two particular orientations.
            for (var x = 0; x < 3; x++) for (var y = 0; y < 3; y++) for (var z = 0; z < 3; z++)
            {
                if (x == y || x == z || y == z) continue;
                for (var mask = 0; mask < 8; mask++)
                {
                    var candidate = Matrix4x4.identity;
                    var axes = new[] { x, y, z };
                    for (var column = 0; column < 3; column++)
                    {
                        var vector = Vector4.zero;
                        vector[axes[column]] = ((mask & (1 << column)) == 0 ? 1f : -1f) / scale;
                        candidate.SetColumn(column, vector);
                    }
                    candidates.Add(candidate);
                }
            }
            var best = float.PositiveInfinity;
            var answer = candidates[0];
            foreach (var candidate in candidates)
            {
                var error = 0f;
                foreach (var pair in pairs)
                {
                    var geometry = pair.sdk.GetMesh();
                    var world = GeometryWorld(pair.sdk);
                    var points = Enumerable.Range(0, geometry.GetControlPointsCount()).Select(i => world.MultiplyPoint3x4(Vec(geometry.GetControlPointAt(i)))).ToArray();
                    var vertices = pair.filter.sharedMesh.vertices;
                    var toWorld = candidate * pair.filter.transform.localToWorldMatrix;
                    for (var i = 0; i < vertices.Length; i += Math.Max(1, vertices.Length / 12))
                    {
                        var point = toWorld.MultiplyPoint3x4(vertices[i]);
                        error = Mathf.Max(error, points.Min(p => (p - point).sqrMagnitude));
                    }
                }
                if (error < best) { best = error; answer = candidate; }
            }
            if (best > 0.000004f) throw new InvalidOperationException(BridgeLocalization.Text("Не совпала система координат импортированного FBX: ") + path + BridgeLocalization.Text(". Исходник не изменён."));
            return answer;
        }

        internal static void Capture(BridgeSession session, GameObject prefabContents)
        {
            if (session.looseMeshes == null) session.looseMeshes = new List<BridgeLooseMesh>();
            session.fbxFiles.Clear(); session.fbxBindings.Clear(); session.looseMeshes.Clear();
            var documents = new Dictionary<string, Document>();
            try
            {
                foreach (var entry in session.nodes.Where(n => n.hasMesh))
                {
                    var transform = MaxBridgeWindow.FindIndexed(prefabContents.transform, entry.path);
                    var filter = transform.GetComponent<MeshFilter>();
                    var path = AssetDatabase.GetAssetPath(filter.sharedMesh);
                    if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    {
                        CaptureLoose(session, entry, prefabContents.transform, transform, null, null, null);
                        continue;
                    }
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    var native = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (!documents.TryGetValue(guid, out var document))
                    {
                        document = new Document(path); documents.Add(guid, document);
                        var file = new SourceFbxFile { guid = guid, path = path, expectedHash = Hash(path), expectedMetaHash = Hash(path + ".meta"), nativeToSdk = MatchAxes(document.scene, native, path), identities = Identities(path) };
                        session.fbxFiles.Add(file);
                        file.identities = RegisteredIdentities(session, file);
                        var backup = Path.Combine(session.directory, "InitialSources", guid);
                        Directory.CreateDirectory(backup);
                        File.Copy(path, Path.Combine(backup, Path.GetFileName(path)));
                        File.Copy(path + ".meta", Path.Combine(backup, Path.GetFileName(path) + ".meta"));
                    }
                    var fileRecord = session.fbxFiles.Single(f => f.guid == guid);
                    var nativeFilter = native.GetComponentsInChildren<MeshFilter>(true).First(f => f.sharedMesh == filter.sharedMesh);
                    var sdkNode = MatchNative(document.scene, nativeFilter);
                    if (sdkNode.GetMesh().GetDeformerCount() != 0) throw new InvalidOperationException(BridgeLocalization.Text("Деформируемые FBX пока не поддерживаются."));
                    var instance = transform;
                    while (instance != null && PrefabUtility.GetCorrespondingObjectFromOriginalSource(instance.gameObject) != native)
                        instance = instance.parent;
                    var directReference = instance == null;
                    if (directReference) instance = transform;
                    var instanceEntry = session.nodes.Single(n => MaxBridgeWindow.FindIndexed(prefabContents.transform, n.path) == instance);
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(filter.sharedMesh, out string _, out long meshId);
                    session.fbxBindings.Add(new SourceFbxBinding
                    {
                        nodeId = entry.id, fileGuid = guid, modelPath = NodePath(sdkNode), meshId = meshId,
                        prefabToMesh = (prefabContents.transform.worldToLocalMatrix * transform.localToWorldMatrix).inverse,
                        meshToSdk = fileRecord.nativeToSdk * nativeFilter.transform.localToWorldMatrix,
                        instancePath = instanceEntry.path, directReference = directReference,
                        materialIds = MaxBridgeWindow.MaterialSlots(transform.GetComponent<MeshRenderer>(), filter.sharedMesh).Select((m, slot) => Id(m, session, slot)).ToList()
                    });
                }
                using (var baseline = new Document(Path.Combine(session.directory, "ToMax.fbx")))
                {
                    var nodes = Tagged(baseline.scene);
                    foreach (var binding in session.fbxBindings)
                    {
                        var node = nodes[binding.nodeId];
                        binding.baselineSignature = ReadGeometry(node, binding.prefabToMesh * GeometryWorld(node), binding.materialIds).Signature();
                    }
                }
                File.WriteAllLines(Path.Combine(session.directory, "ProtectedMeshes.txt"), session.looseMeshes.Select(m => m.nodeId));
                session.prefabFileHash = Hash(session.sourcePath);
            }
            finally { foreach (var document in documents.Values) document.Dispose(); }
        }

        private static Dictionary<string, FbxNode> Tagged(FbxScene scene)
        {
            var result = new Dictionary<string, FbxNode>();
            foreach (var node in Nodes(scene.GetRootNode()))
            {
                var id = MaxBridgeWindow.Token(node.GetName(), MaxBridgeWindow.NodeTag);
                if (id == null) continue;
                if (result.ContainsKey(id)) throw new InvalidOperationException(BridgeLocalization.Text("Повторяется ID объекта Max. Добавь копию через Add selected objects."));
                result.Add(id, node);
            }
            return result;
        }

        private sealed class Geometry
        {
            public readonly List<Vector3> positions = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public readonly List<Color> colors = new List<Color>();
            public readonly List<Vector2>[] uv = Enumerable.Range(0, 8).Select(_ => new List<Vector2>()).ToArray();
            public readonly List<int> slots = new List<int>();
            public bool Equivalent(Geometry other, bool includeMaterials = true, bool allowReorder = true)
            {
                if (slots.Count != other.slots.Count || positions.Count != other.positions.Count) return false;
                if (allowReorder && Signature(includeMaterials) == other.Signature(includeMaterials)) return true;
                if (includeMaterials && !slots.SequenceEqual(other.slots)) return false;
                const float epsilonSquared = 0.00000001f;
                for (var i = 0; i < positions.Count; i++)
                    if ((positions[i] - other.positions[i]).sqrMagnitude > epsilonSquared ||
                        (normals[i] - other.normals[i]).sqrMagnitude > epsilonSquared) return false;
                for (var layer = 0; layer < uv.Length; layer++)
                {
                    if (uv[layer].Count != other.uv[layer].Count) return false;
                    for (var i = 0; i < uv[layer].Count; i++)
                        if ((uv[layer][i] - other.uv[layer][i]).sqrMagnitude > epsilonSquared) return false;
                }
                if (colors.Count != other.colors.Count) return false;
                for (var i = 0; i < colors.Count; i++)
                    if (((Vector4)colors[i] - (Vector4)other.colors[i]).sqrMagnitude > epsilonSquared) return false;
                return true;
            }
            public string Signature(bool includeMaterials = true)
            {
                var rows = new List<string>();
                for (var i = 0; i < slots.Count; i++)
                {
                    rows.Add((includeMaterials ? slots[i] + ":" : "") + TriangleKey(i));
                }
                rows.Sort(StringComparer.Ordinal);
                using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", rows))));
            }
            public string TriangleKey(int i)
            {
                    var vertices = new string[3];
                    for (var j = 0; j < 3; j++)
                    {
                        var k = i * 3 + j; var p = positions[k]; var n = normals[k];
                        var values = new List<float> { p.x, p.y, p.z, n.x, n.y, n.z };
                        foreach (var layer in uv) if (layer.Count != 0) { values.Add(layer[k].x); values.Add(layer[k].y); }
                        if (colors.Count != 0) { var c = colors[k]; values.AddRange(new[] { c.r, c.g, c.b, c.a }); }
                        vertices[j] = string.Join(",", values.Select(v => Math.Round(v * 10000).ToString(CultureInfo.InvariantCulture)));
                    }
                    // Cyclic polygon order is immaterial; winding remains significant.
                    var variants = Enumerable.Range(0, 3).Select(j => vertices[j] + ";" + vertices[(j + 1) % 3] + ";" + vertices[(j + 2) % 3]);
                    return variants.OrderBy(v => v, StringComparer.Ordinal).First();
            }
            public void Transform(Matrix4x4 matrix)
            {
                var normalMatrix = matrix.inverse.transpose;
                for (var i = 0; i < positions.Count; i++)
                { positions[i] = matrix.MultiplyPoint3x4(positions[i]); normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized; }
                if (matrix.determinant < 0)
                    for (var i = 0; i < positions.Count; i += 3)
                    {
                        Swap(positions, i + 1, i + 2); Swap(normals, i + 1, i + 2);
                        if (colors.Count != 0) Swap(colors, i + 1, i + 2);
                        foreach (var layer in uv) if (layer.Count != 0) Swap(layer, i + 1, i + 2);
                    }
            }
            private static void Swap<T>(List<T> list, int a, int b) { var value = list[a]; list[a] = list[b]; list[b] = value; }
        }

        private static Matrix4x4 Calibration(Geometry original, Geometry maxBaseline)
        {
            if (original.positions.Count != maxBaseline.positions.Count || !original.slots.SequenceEqual(maxBaseline.slots))
                throw new InvalidOperationException(BridgeLocalization.Text("Max изменил исходную топологию при импорте. Синхронизация остановлена до записи FBX."));
            var from = maxBaseline.positions; var to = original.positions;
            if (from.Count == 0) return Matrix4x4.identity;
            var p0 = from[0]; var q0 = to[0];
            var one = Enumerable.Range(0, from.Count).OrderByDescending(i => (from[i] - p0).sqrMagnitude).First();
            var two = Enumerable.Range(0, from.Count).OrderByDescending(i => Vector3.Cross(from[one] - p0, from[i] - p0).sqrMagnitude).First();
            var normal = Vector3.Cross(from[one] - p0, from[two] - p0);
            if (normal.sqrMagnitude < 1e-16f) throw new InvalidOperationException(BridgeLocalization.Text("В меше нет невырожденных полигонов."));
            var three = Enumerable.Range(0, from.Count).OrderByDescending(i => Mathf.Abs(Vector3.Dot(normal.normalized, from[i] - p0))).First();
            var p3 = from[three]; var q3 = to[three];
            if (Mathf.Abs(Vector3.Dot(normal.normalized, p3 - p0)) < 0.00001f)
            {
                var targetNormal = Vector3.Cross(to[one] - q0, to[two] - q0);
                var normalScale = Mathf.Sqrt(targetNormal.magnitude / normal.magnitude);
                p3 = p0 + normal.normalized;
                q3 = q0 + targetNormal.normalized * normalScale;
            }
            var a = Matrix4x4.identity; var b = Matrix4x4.identity;
            a.SetColumn(0, (Vector4)(from[one] - p0)); a.SetColumn(1, (Vector4)(from[two] - p0)); a.SetColumn(2, (Vector4)(p3 - p0)); a.SetColumn(3, new Vector4(p0.x, p0.y, p0.z, 1));
            b.SetColumn(0, (Vector4)(to[one] - q0)); b.SetColumn(1, (Vector4)(to[two] - q0)); b.SetColumn(2, (Vector4)(q3 - q0)); b.SetColumn(3, new Vector4(q0.x, q0.y, q0.z, 1));
            var result = b * a.inverse;
            if (Mathf.Abs(a.determinant) < 1e-12f) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось восстановить систему координат меша Max."));
            for (var i = 0; i < from.Count; i++)
                if (Vector3.Distance(result.MultiplyPoint3x4(from[i]), to[i]) > .001f)
                    throw new InvalidOperationException(BridgeLocalization.Text("Max изменил порядок вершин или нелинейно исказил исходный меш при импорте. Исходный FBX не изменён."));
            return result;
        }
        private static int LayerIndex(FbxLayerElement layer, int polygon, int corner, int point)
        {
            switch (layer.GetMappingMode())
            {
                case FbxLayerElement.EMappingMode.eByControlPoint: return point;
                case FbxLayerElement.EMappingMode.eByPolygonVertex: return polygon * 3 + corner;
                case FbxLayerElement.EMappingMode.eByPolygon: return polygon;
                case FbxLayerElement.EMappingMode.eAllSame: return 0;
                default: throw new InvalidOperationException(BridgeLocalization.Text("Не поддерживается способ разметки FBX-слоя."));
            }
        }
        private static Geometry ReadGeometry(FbxNode node, Matrix4x4 matrix, List<string> slotIds)
        {
            var mesh = node.GetMesh();
            if (mesh == null) throw new InvalidOperationException(BridgeLocalization.Text("Потерян меш: ") + node.GetName());
            var result = new Geometry();
            var normalMatrix = matrix.inverse.transpose;
            var materialLayer = mesh.GetLayerCount() == 0 ? null : mesh.GetLayer(0).GetMaterials();
            for (var polygon = 0; polygon < mesh.GetPolygonCount(); polygon++)
            {
                if (mesh.GetPolygonSize(polygon) != 3) throw new InvalidOperationException(BridgeLocalization.Text("FBX не триангулирован."));
                var materialIndex = materialLayer == null ? 0 : LayerIndex(materialLayer, polygon, 0, 0);
                if (materialLayer != null && (materialIndex < 0 || materialIndex >= materialLayer.GetIndexArray().GetCount()))
                    throw new InvalidOperationException(BridgeLocalization.Text("Объект «") + node.GetName() + BridgeLocalization.Text("»: отсутствует material ID полигона ") + (polygon + 1) + BridgeLocalization.Text(". Проверь назначения материалов в Max."));
                var slot = materialLayer == null ? 0 : materialLayer.GetIndexArray().GetAt(materialIndex);
                var material = slot >= 0 && slot < node.GetMaterialCount() ? node.GetMaterial(slot) : null;
                var token = material == null ? null : MaxBridgeWindow.Token(material.GetName(), MaxBridgeWindow.MaterialTag);
                var mapped = token == null ? -1 : slotIds.IndexOf(token);
                if (mapped < 0) throw new InvalidOperationException(BridgeLocalization.Text("Объект «") + node.GetName() + "», material ID " + (slot + 1) + BridgeLocalization.Text(", полигон ") + (polygon + 1) + BridgeLocalization.Text(": материал не сопоставлен с Unity. Отправь нужный Unity-материал через палитру или оставь слот пустым в Max и повтори экспорт."));
                result.slots.Add(mapped);
                for (var corner = 0; corner < 3; corner++)
                {
                    var point = mesh.GetPolygonVertex(polygon, corner);
                    result.positions.Add(matrix.MultiplyPoint3x4(Vec(mesh.GetControlPointAt(point))));
                    if (!mesh.GetPolygonVertexNormal(polygon, corner, out var normal)) throw new InvalidOperationException(BridgeLocalization.Text("В FBX нет нормалей."));
                    result.normals.Add(normalMatrix.MultiplyVector(Vec(normal)).normalized);
                    for (var channel = 0; channel < Math.Min(mesh.GetLayerCount(), 8); channel++)
                    {
                        var layer = mesh.GetLayer(channel).GetUVs();
                        if (layer == null) continue;
                        var index = LayerIndex(layer, polygon, corner, point);
                        if (layer.GetReferenceMode() != FbxLayerElement.EReferenceMode.eDirect) index = layer.GetIndexArray().GetAt(index);
                        var value = layer.GetDirectArray().GetAt(index);
                        result.uv[channel].Add(new Vector2((float)value[0], (float)value[1]));
                    }
                    var color = mesh.GetLayerCount() == 0 ? null : mesh.GetLayer(0).GetVertexColors();
                    if (color != null)
                    {
                        var index = LayerIndex(color, polygon, corner, point);
                        if (color.GetReferenceMode() != FbxLayerElement.EReferenceMode.eDirect) index = color.GetIndexArray().GetAt(index);
                        var c = color.GetDirectArray().GetAt(index);
                        result.colors.Add(new Color((float)c.mRed, (float)c.mGreen, (float)c.mBlue, (float)c.mAlpha));
                    }
                }
            }
            return result;
        }
        private static void SetGeometry(FbxScene scene, FbxNode node, Geometry data)
        {
            var previous = node.GetMesh();
            var mesh = FbxMesh.Create(scene, previous == null ? node.GetName() + "_Mesh" : previous.GetName());
            mesh.InitControlPoints(data.positions.Count);
            for (var i = 0; i < data.positions.Count; i++)
            { var p = data.positions[i]; mesh.SetControlPointAt(new FbxVector4(p.x, p.y, p.z, 1), i); }
            mesh.CreateLayer();
            var normals = FbxLayerElementNormal.Create(mesh, "Normals");
            normals.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);
            normals.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
            foreach (var n in data.normals) normals.GetDirectArray().Add(new FbxVector4(n.x, n.y, n.z, 0));
            mesh.GetLayer(0).SetNormals(normals);
            var materials = FbxLayerElementMaterial.Create(mesh, "Materials");
            materials.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygon);
            materials.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
            for (var i = 0; i < data.slots.Count; i++)
            {
                mesh.BeginPolygon(data.slots[i], -1, -1, false);
                mesh.AddPolygon(i * 3); mesh.AddPolygon(i * 3 + 1); mesh.AddPolygon(i * 3 + 2); mesh.EndPolygon();
                materials.GetIndexArray().Add(data.slots[i]);
            }
            mesh.GetLayer(0).SetMaterials(materials);
            for (var channel = 0; channel < 8; channel++)
            {
                if (data.uv[channel].Count == 0) continue;
                while (mesh.GetLayerCount() <= channel) mesh.CreateLayer();
                var uv = FbxLayerElementUV.Create(mesh, "UVSet" + channel);
                uv.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);
                uv.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
                foreach (var value in data.uv[channel]) uv.GetDirectArray().Add(new FbxVector2(value.x, value.y));
                mesh.GetLayer(channel).SetUVs(uv);
            }
            if (data.colors.Count != 0)
            {
                var colors = FbxLayerElementVertexColor.Create(mesh, "Colors");
                colors.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);
                colors.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
                foreach (var c in data.colors) colors.GetDirectArray().Add(new FbxColor(c.r, c.g, c.b, c.a));
                mesh.GetLayer(0).SetVertexColors(colors);
            }
            node.SetNodeAttribute(mesh);
        }

        private sealed class NewNode
        {
            public string id, modelPath, parentInstancePath, fileGuid;
            public Matrix4x4 sdkToPrefab;
            public List<string> materialIds;
            public List<Vector3> expectedPositions;
            public Geometry expectedGeometry;
            public bool inSource, directParent;
        }
        private static Bounds BoundsOf(IEnumerable<Vector3> points)
        {
            var all = points.ToArray();
            if (all.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var result = new Bounds(all[0], Vector3.zero);
            for (var i = 1; i < all.Length; i++) result.Encapsulate(all[i]);
            return result;
        }
        private static string IndexedPath(Transform root, Transform node)
        {
            var indices = new List<int>();
            while (node != root) { indices.Add(node.GetSiblingIndex()); node = node.parent; }
            indices.Reverse(); return string.Join("/", indices);
        }

        private sealed class SourceIdentityChangedException : InvalidOperationException
        {
            public SourceIdentityChangedException(string message) : base(message) { }
        }

        private static bool IsPreviewMaterial(BridgeSession session, SourceFbxFile file, SourceIdentity identity)
        {
            if (identity.type != typeof(Material).FullName) return false;
            if (identity.emptySlot) return true;
            var token = MaxBridgeWindow.Token(identity.name, MaxBridgeWindow.MaterialTag);
            if (token == null) return false;
            var record = session.materials.FirstOrDefault(m => m.id == token);
            if (record == null && !string.IsNullOrEmpty(identity.materialGuid))
                record = new BridgeMaterial { guid = identity.materialGuid, localId = identity.materialLocalId };
            if (record == null)
                foreach (var directory in Directory.EnumerateDirectories(Path.GetDirectoryName(session.directory)))
                {
                    var manifest = Path.Combine(directory, "session.json");
                    if (!File.Exists(manifest)) continue;
                    var previous = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(manifest));
                    foreach (var candidate in (previous?.materials ?? new List<BridgeMaterial>()).Where(m => m.id == token))
                    {
                        if (record != null && (record.guid != candidate.guid || record.localId != candidate.localId))
                            throw new InvalidOperationException(BridgeLocalization.Text("Неоднозначная история материала bridge: ") + identity.name);
                        record = candidate;
                    }
                }
            if (record != null && record.emptySlot) { identity.emptySlot = true; return true; }
            if (record == null || string.IsNullOrEmpty(record.guid)) return false;
            // Retain provenance across sessions, including after old sessions are removed.
            identity.materialGuid = record.guid;
            identity.materialLocalId = record.localId;
            if (record.guid == file.guid && record.localId == identity.id) return false;
            var path = AssetDatabase.GUIDToAssetPath(record.guid);
            return !string.IsNullOrEmpty(path) && AssetDatabase.LoadAllAssetsAtPath(path).Any(asset =>
                asset is Material && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long id) && id == record.localId);
        }

        private static List<SourceIdentity> RegisteredIdentities(BridgeSession session, SourceFbxFile file)
        {
            var identities = Identities(file.path);
            foreach (var identity in identities.Where(i => i.type == typeof(Material).FullName))
            {
                var previous = file.identities.FirstOrDefault(i => i.id == identity.id && i.type == identity.type && i.name == identity.name);
                if (previous != null) { identity.materialGuid = previous.materialGuid; identity.materialLocalId = previous.materialLocalId; identity.emptySlot = previous.emptySlot; }
                IsPreviewMaterial(session, file, identity);
            }
            return identities;
        }

        internal static void Apply(BridgeSession session)
        {
            BridgeLog.Write(session, "apply.start", "models=" + session.fbxFiles.Count + "; meshes=" + session.fbxBindings.Count);
            var snapshot = JsonUtility.ToJson(session);
            try { ApplyCore(session); }
            catch
            {
                // Keep session metadata consistent with the rolled-back assets.
                JsonUtility.FromJsonOverwrite(snapshot, session);
                throw;
            }
        }

        private static void ApplyCore(BridgeSession session)
        {
            if (session.looseMeshes == null) session.looseMeshes = new List<BridgeLooseMesh>();
            if (session.fbxFiles.Count == 0 && session.looseMeshes.Count == 0) throw new InvalidOperationException(BridgeLocalization.Text("Отправь prefab заново для работы с исходным FBX."));
            if (Hash(session.sourcePath) != session.prefabFileHash) throw new InvalidOperationException(BridgeLocalization.Text("Prefab изменён в Unity. Отправь актуальную версию в Max заново."));
            foreach (var file in session.fbxFiles)
                if (Hash(file.path) != file.expectedHash || (!string.IsNullOrEmpty(file.expectedMetaHash) && Hash(file.path + ".meta") != file.expectedMetaHash))
                    throw new InvalidOperationException(BridgeLocalization.Text("Исходный FBX или его настройки импорта изменены вне bridge: ") + file.path + BridgeLocalization.Text(". Отправь prefab заново."));
            var editedPath = Path.Combine(session.directory, "Edited.fbx");
            if (!File.Exists(editedPath)) throw new InvalidOperationException(BridgeLocalization.Text("Сохрани .max через Ctrl+S или нажми Update now в Max."));
            var maxBaselinePath = Path.Combine(session.directory, "MaxBaseline.fbx");
            if (!File.Exists(maxBaselinePath)) throw new InvalidOperationException(BridgeLocalization.Text("Нет исходного снимка Max. Запусти обновлённый Connect_Max.ms и отправь prefab заново."));
            var sources = new Dictionary<string, Document>();
            var candidates = new Dictionary<string, string>();
            var backups = new Dictionary<string, string>();
            var files = new List<SourceFbxFile>(session.fbxFiles);
            var looseUpdates = new List<BridgeLooseMesh>();
            var looseGeometry = new Dictionary<string, Geometry>();
            var newNodes = new List<NewNode>();
            var dirtyFiles = new HashSet<string>();
            var removed = new List<SourceFbxBinding>();
            var removedIds = new Dictionary<string, HashSet<long>>();
            var expandedMaterials = new Dictionary<string, List<string>>();
            var appliedSignatures = new Dictionary<string, string>();
            var appliedGeometrySignatures = new Dictionary<string, string>();
            var materialMappings = new Dictionary<string, List<SourceMaterialSlot>>();
            var backupDirectory = Path.Combine(session.directory, "Backups", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff"));
            Directory.CreateDirectory(backupDirectory);
            BridgeLog.Write(session, "apply.backup", backupDirectory);
            try
            {
                using (var returned = new Document(editedPath))
                using (var baseline = new Document(Path.Combine(session.directory, "ToMax.fbx")))
                using (var maxBaseline = new Document(maxBaselinePath))
                {
                    NormalizeReturnedMaterials(session, returned.scene);
                    var remote = Tagged(returned.scene);
                    if (!remote.Values.Any(n => n.GetMesh() != null))
                        throw new InvalidOperationException(BridgeLocalization.Text("Из Max получена пустая геометрия. Отправь prefab в Max заново; исходные assets не изменены."));
                    var initial = Tagged(baseline.scene);
                    var initialMax = Tagged(maxBaseline.scene);
                    var calibrationBinding = session.fbxBindings.FirstOrDefault(b => initial.ContainsKey(b.nodeId) && initialMax.ContainsKey(b.nodeId));
                    var maxToPrefab = calibrationBinding == null ? Matrix4x4.identity : Calibration(
                        ReadGeometry(initial[calibrationBinding.nodeId], GeometryWorld(initial[calibrationBinding.nodeId]), calibrationBinding.materialIds),
                        ReadGeometry(initialMax[calibrationBinding.nodeId], GeometryWorld(initialMax[calibrationBinding.nodeId]), calibrationBinding.materialIds));
                    foreach (var file in session.fbxFiles) sources.Add(file.guid, new Document(file.path));
                    foreach (var binding in session.fbxBindings)
                    {
                        var map = (binding.sourceMaterialSlots ?? new List<SourceMaterialSlot>()).Select(s => new SourceMaterialSlot { index = s.index, materialId = s.materialId, originalMaterialId = s.originalMaterialId ?? s.materialId }).ToList();
                        if (map.Count == 0)
                        {
                            var file = session.fbxFiles.Single(f => f.guid == binding.fileGuid);
                            var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
                            var filter = native.GetComponentsInChildren<MeshFilter>(true).First(f => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(f.sharedMesh, out string _, out long id) && id == binding.meshId);
                            var indices = ImportedSdkSlots(file, filter);
                            var target = Find(sources[file.guid].scene, binding.modelPath);
                            for (var submesh = 0; submesh < indices.Count; submesh++)
                            {
                                var index = indices[submesh];
                                var token = index < target.GetMaterialCount() ? MaxBridgeWindow.Token(target.GetMaterial(index).GetName(), MaxBridgeWindow.MaterialTag) : null;
                                var materialId = token != null && binding.materialIds.Contains(token) ? token : binding.materialIds[submesh];
                                map.Add(new SourceMaterialSlot { index = index, materialId = materialId, originalMaterialId = materialId });
                            }
                        }
                        materialMappings.Add(binding.nodeId, map);
                    }
                    foreach (var binding in session.fbxBindings.Where(b => !remote.ContainsKey(b.nodeId)))
                    {
                        if (initial.ContainsKey(binding.nodeId) && !binding.bridgeCreated)
                            throw new InvalidOperationException(BridgeLocalization.Text("Удалён исходный меш в Max. Удаление исходных мешей пока не поддерживается."));
                        var sdkNode = Find(sources[binding.fileGuid].scene, binding.modelPath);
                        if (sdkNode.GetChildCount() != 0)
                            throw new InvalidOperationException(BridgeLocalization.Text("Нельзя удалить объект с дочерними узлами: ") + binding.modelPath);
                        var file = session.fbxFiles.Single(f => f.guid == binding.fileGuid);
                        var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
                        var filters = native.GetComponentsInChildren<MeshFilter>(true);
                        var filter = filters.Single(f => f.name == sdkNode.GetName());
                        if (filters.Any(f => f != filter && f.sharedMesh == filter.sharedMesh))
                            throw new InvalidOperationException(BridgeLocalization.Text("Удаляемый объект использует общий меш: ") + binding.modelPath);
                        if (!removedIds.TryGetValue(file.guid, out var ids)) removedIds.Add(file.guid, ids = new HashSet<long>());
                        foreach (var asset in new Object[] { filter.gameObject, filter.sharedMesh }.Concat(filter.GetComponents<Component>()))
                            if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long id)) ids.Add(id);
                        removed.Add(binding);
                    }
                    foreach (var group in session.fbxBindings.Except(removed).GroupBy(b => b.fileGuid + ":" + b.meshId))
                    {
                        var variants = new List<Tuple<SourceFbxBinding, Geometry, bool>>();
                        var rendererOverrides = new Dictionary<string, List<SourceMaterialSlot>>();
                        var partitionSources = new Dictionary<string, int[]>();
                        foreach (var binding in group)
                        {
                            if (!remote.TryGetValue(binding.nodeId, out var node)) throw new InvalidOperationException(BridgeLocalization.Text("Удалён исходный меш в Max. Удаление пока не поддерживается."));
                            var slots = new List<string>(binding.materialIds);
                            // Attach can bring materials from any other object in this FBX.
                            // Retain the existing slot order and append known Unity materials.
                            for (var i = 0; i < node.GetMaterialCount(); i++)
                            {
                                var id = MaxBridgeWindow.Token(node.GetMaterial(i).GetName(), MaxBridgeWindow.MaterialTag);
                                if (id != null && !slots.Contains(id) && session.materials.Any(m => m.id == id)) slots.Add(id);
                            }
                            expandedMaterials[binding.nodeId] = slots;
                            var data = ReadGeometry(node, binding.prefabToMesh * GeometryWorld(node), slots);
                            if (binding.bridgeCreated && !initial.ContainsKey(binding.nodeId))
                                data = ReadGeometry(node, binding.prefabToMesh * maxToPrefab * GeometryWorld(node), slots);
                            var returnedSignature = data.Signature();
                            appliedSignatures[binding.nodeId] = returnedSignature;
                            appliedGeometrySignatures[binding.nodeId] = data.Signature(false);
                            if (returnedSignature == binding.lastAppliedSignature)
                                rendererOverrides[binding.nodeId] = materialMappings[binding.nodeId];
                            var isChanged = true;
                            if (initial.TryGetValue(binding.nodeId, out var original))
                            {
                                if (!initialMax.TryGetValue(binding.nodeId, out var maxOriginal)) throw new InvalidOperationException(BridgeLocalization.Text("Неполный исходный снимок Max. Отправь prefab заново."));
                                var originalData = ReadGeometry(original, binding.prefabToMesh * GeometryWorld(original), binding.materialIds);
                                var importedData = ReadGeometry(maxOriginal, binding.prefabToMesh * GeometryWorld(maxOriginal), binding.materialIds);
                                var file = session.fbxFiles.Single(f => f.guid == binding.fileGuid);
                                var initialSource = Path.Combine(session.directory, "InitialSources", file.guid, Path.GetFileName(file.path));
                                var sourceUnchanged = File.Exists(initialSource) && Hash(file.path) == Hash(initialSource);
                                // Identical face attributes prove a material-only edit.
                                // Retain original physical slots to preserve embedded asset IDs.
                                if ((sourceUnchanged || binding.lastAppliedGeometrySignature == appliedGeometrySignatures[binding.nodeId]) && data.Equivalent(importedData, false))
                                {
                                    var replacements = new Dictionary<string, string>();
                                    var uniform = true;
                                    var ordered = data.Equivalent(importedData, false, false);
                                    var originalFaces = ordered ? null : Enumerable.Range(0, importedData.slots.Count)
                                        .GroupBy(importedData.TriangleKey).ToDictionary(g => g.Key, g => g.Select(f => binding.materialIds[importedData.slots[f]]).Distinct().ToArray());
                                    var sourceFaces = new int[data.slots.Count];
                                    var knownSources = true;
                                    for (var face = 0; face < data.slots.Count; face++)
                                    {
                                        string before;
                                        if (ordered) before = binding.materialIds[importedData.slots[face]];
                                        else
                                        {
                                            if (!originalFaces.TryGetValue(data.TriangleKey(face), out var faceMaterials) || faceMaterials.Length != 1) { uniform = false; knownSources = false; break; }
                                            before = faceMaterials[0];
                                        }
                                        var after = slots[data.slots[face]];
                                        var originalSlots = materialMappings[binding.nodeId].Where(s => s.originalMaterialId == before).ToArray();
                                        if (originalSlots.Length != 1) knownSources = false;
                                        else sourceFaces[face] = originalSlots[0].index;
                                        if (replacements.TryGetValue(before, out var previous) && previous != after) uniform = false;
                                        replacements[before] = after;
                                    }
                                    if (uniform && sourceUnchanged)
                                        rendererOverrides[binding.nodeId] = materialMappings[binding.nodeId].Select(s => new SourceMaterialSlot
                                        {
                                            index = s.index, originalMaterialId = s.originalMaterialId,
                                            materialId = replacements.TryGetValue(s.originalMaterialId, out var replacement) ? replacement : s.materialId
                                        }).ToList();
                                    else if (knownSources) partitionSources[binding.nodeId] = sourceFaces;
                                }
                                var correction = Calibration(originalData, importedData);
                                isChanged = string.IsNullOrEmpty(binding.lastAppliedSignature)
                                    ? !data.Equivalent(importedData)
                                    : returnedSignature != binding.lastAppliedSignature;
                                data.Transform(correction);
                            }
                            variants.Add(Tuple.Create(binding, data, isChanged));
                        }
                        if (rendererOverrides.Count == variants.Count)
                        {
                            foreach (var pair in rendererOverrides) materialMappings[pair.Key] = pair.Value;
                            BridgeLog.Write(session, "apply.material-overrides", group.Key);
                            continue;
                        }
                        var changed = variants.Where(v => v.Item3).ToList();
                        if (changed.Count > 1 && changed.Skip(1).Any(v => !v.Item2.Equivalent(changed[0].Item2)))
                            throw new InvalidOperationException(BridgeLocalization.Text("Разные правки экземпляров одного меша: ") + group.First().modelPath + BridgeLocalization.Text(". Один меш FBX должен иметь одну геометрию."));
                        var chosen = changed.Count != 0 ? changed[0] : variants[0];
                        var chosenSlots = expandedMaterials[chosen.Item1.nodeId];
                        if (changed.Count == 0 && chosenSlots.SequenceEqual(chosen.Item1.materialIds)) continue;
                        var source = sources[chosen.Item1.fileGuid];
                        var target = Find(source.scene, chosen.Item1.modelPath);
                        if (chosen.Item1.bridgeCreated || !initial.ContainsKey(chosen.Item1.nodeId)) source.NormalizeAddedRoot(target);
                        chosen.Item2.Transform(GeometryWorld(target).inverse * chosen.Item1.meshToSdk);
                        if (partitionSources.TryGetValue(chosen.Item1.nodeId, out var originalSdkFaces))
                            RemapMaterialPartitions(session, source.scene, target, chosenSlots, chosen.Item2, originalSdkFaces, materialMappings[chosen.Item1.nodeId]);
                        else RemapMaterialSlots(session, source.scene, target, chosenSlots, chosen.Item2, chosen.Item1.fileGuid, materialMappings[chosen.Item1.nodeId]);
                        SetGeometry(source.scene, target, chosen.Item2);
                        dirtyFiles.Add(chosen.Item1.fileGuid);
                    }
                    PrepareLoose(session, remote, initial, initialMax, files, sources, looseUpdates, looseGeometry);
                    foreach (var pair in remote.Where(p => p.Value.GetMesh() != null && !session.fbxBindings.Any(b => b.nodeId == p.Key) && !session.looseMeshes.Any(b => b.nodeId == p.Key)))
                    {
                        SourceFbxBinding anchor = null;
                        for (var parent = pair.Value.GetParent(); parent != null && anchor == null; parent = parent.GetParent())
                        {
                            var id = MaxBridgeWindow.Token(parent.GetName(), MaxBridgeWindow.NodeTag);
                            anchor = session.fbxBindings.Find(b => b.nodeId == id);
                        }
                        // A mesh parent explicitly selects its source FBX. An unparented
                        // addition gets a separate file beside the first model in the session.
                        var inSource = anchor != null;
                        var locationFile = inSource ? files.Single(f => f.guid == anchor.fileGuid)
                            : session.fbxFiles.FirstOrDefault();
                        var originalName = pair.Value.GetName();
                        var name = originalName.Substring(0, originalName.LastIndexOf(MaxBridgeWindow.NodeTag, StringComparison.Ordinal));
                        var safeName = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
                        if (string.IsNullOrEmpty(safeName)) safeName = "Mesh";
                        var modelDirectory = Path.GetDirectoryName(locationFile?.path ?? session.sourcePath).Replace('\\', '/');
                        if (!modelDirectory.StartsWith("Assets/", StringComparison.Ordinal) && modelDirectory != "Assets")
                            modelDirectory = Path.GetDirectoryName(session.sourcePath).Replace('\\', '/');
                        var stem = modelDirectory + "/" + Path.GetFileNameWithoutExtension(session.sourcePath) + "_" + safeName;
                        var path = AssetDatabase.GenerateUniqueAssetPath(stem + ".fbx");
                        if (files.Any(f => string.Equals(f.path, path, StringComparison.OrdinalIgnoreCase))) path = stem + "_" + pair.Key.Substring(0, 8) + ".fbx";
                        var file = inSource ? files.Single(f => f.guid == anchor.fileGuid) : new SourceFbxFile { guid = Guid.NewGuid().ToString("N"), path = path, expectedHash = "", expectedMetaHash = "", nativeToSdk = Matrix4x4.identity };
                        var document = inSource ? sources[file.guid] : new Document();
                        if (!inSource)
                        {
                            files.Add(file);
                            document.scene.GetRootNode().AddChild(FbxNode.Create(document.scene, Path.GetFileNameWithoutExtension(path)));
                            sources.Add(file.guid, document);
                        }
                        if (Nodes(document.scene.GetRootNode()).Any(n => n.GetName() == name)) name += "_" + pair.Key.Substring(0, 8);
                        var newNode = FbxNode.Create(document.scene, name);
                        (inSource ? Find(document.scene, anchor.modelPath) : document.scene.GetRootNode().GetChild(0)).AddChild(newNode);
                        var ids = new List<string>();
                        for (var i = 0; i < pair.Value.GetMaterialCount(); i++)
                        {
                            var id = MaxBridgeWindow.Token(pair.Value.GetMaterial(i).GetName(), MaxBridgeWindow.MaterialTag);
                            if (!session.materials.Any(m => m.id == id)) throw new InvalidOperationException(BridgeLocalization.Text("Материал нового объекта не зарегистрирован в Unity bridge."));
                            newNode.AddMaterial(FbxSurfacePhong.Create(document.scene, session.materials.Single(m => m.id == id).name + MaxBridgeWindow.MaterialTag + id));
                            ids.Add(id);
                        }
                        var parentInstancePath = anchor?.instancePath ?? "";
                        var expectedGeometry = ReadGeometry(pair.Value, maxToPrefab * GeometryWorld(pair.Value), ids);
                        var sourceFrame = inSource ? anchor.meshToSdk * anchor.prefabToMesh : Matrix4x4.identity;
                        var prefabGeometry = ReadGeometry(pair.Value, maxToPrefab * GeometryWorld(pair.Value), ids);
                        prefabGeometry.Transform(GeometryWorld(newNode).inverse * sourceFrame);
                        SetGeometry(document.scene, newNode, prefabGeometry);
                        dirtyFiles.Add(file.guid);
                        newNodes.Add(new NewNode { id = pair.Key, modelPath = NodePath(newNode), parentInstancePath = parentInstancePath, fileGuid = file.guid, materialIds = ids, sdkToPrefab = sourceFrame.inverse, directParent = inSource && anchor.directReference, expectedPositions = new List<Vector3>(expectedGeometry.positions), expectedGeometry = expectedGeometry, inSource = inSource });
                    }
                }
                foreach (var update in looseUpdates) dirtyFiles.Add(update.fileGuid);
                foreach (var binding in removed)
                {
                    var node = Find(sources[binding.fileGuid].scene, binding.modelPath);
                    node.GetParent().RemoveChild(node);
                    node.Destroy();
                    dirtyFiles.Add(binding.fileGuid);
                }
                foreach (var file in files.Where(f => dirtyFiles.Contains(f.guid)))
                {
                    var candidate = Path.Combine(backupDirectory, file.guid + "_candidate.fbx");
                    sources[file.guid].Save(candidate); candidates.Add(file.guid, candidate);
                    if (File.Exists(file.path))
                    {
                        var backup = Path.Combine(backupDirectory, file.guid + "_original.fbx");
                        File.Copy(file.path, backup); File.Copy(file.path + ".meta", backup + ".meta"); backups.Add(file.guid, backup);
                    }
                }
                File.Copy(session.sourcePath, Path.Combine(backupDirectory, "original.prefab"));
                File.Copy(session.sourcePath + ".meta", Path.Combine(backupDirectory, "original.prefab.meta"));
                // Imported additions receive a new Unity GUID during registration.
                // Capture rollback paths now, before those mutable IDs change.
                var rollbackFiles = files.Where(f => dirtyFiles.Contains(f.guid))
                    .ToDictionary(f => f.path, f => backups.TryGetValue(f.guid, out var backup) ? backup : null);
                try
                {
                    foreach (var file in files.Where(f => dirtyFiles.Contains(f.guid)))
                    {
                        // Stay on the source volume without requiring drive-root permissions.
                        // Unity ignores the dot-prefixed staging folder next to the source.
                        var temporaryDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file.path)), ".Unity3dsMaxBridgeTemp", session.id);
                        Directory.CreateDirectory(temporaryDirectory);
                        var temporary = Path.Combine(temporaryDirectory, file.guid + ".fbx");
                        try
                        {
                            File.Copy(candidates[file.guid], temporary, true);
                            if (File.Exists(file.path)) File.Replace(temporary, file.path, null);
                            else File.Move(temporary, file.path);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        AssetDatabase.ImportAsset(file.path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        var actual = Identities(file.path);
                        foreach (var identity in file.identities)
                            if (!(removedIds.TryGetValue(file.guid, out var deleted) && deleted.Contains(identity.id)) && !IsPreviewMaterial(session, file, identity) && !actual.Any(a => a.id == identity.id && a.type == identity.type && a.name == identity.name))
                                throw new SourceIdentityChangedException(BridgeLocalization.Text("Изменилась ссылка Unity на «") + identity.name + BridgeLocalization.Text("». Исходник будет восстановлен. Если добавляешь дочерний меш, убери его связь с моделью в Max и повтори Update now: он будет сохранён отдельным FBX."));
                    }
                    UpdateMaterials(session, expandedMaterials, removed, materialMappings);
                    var generatedPaths = files.Where(f => string.IsNullOrEmpty(f.expectedHash)).ToDictionary(f => f.guid, f => f.path);
                    RegisterLoose(session, files, looseUpdates, looseGeometry);
                    foreach (var generated in generatedPaths)
                    {
                        var actualGuid = AssetDatabase.AssetPathToGUID(generated.Value);
                        var file = files.Single(f => f.path == generated.Value);
                        file.guid = actualGuid;
                        using (var normalized = new Document(file.path))
                            file.nativeToSdk = MatchAxes(normalized.scene, AssetDatabase.LoadAssetAtPath<GameObject>(file.path), file.path);
                        foreach (var added in newNodes.Where(n => n.fileGuid == generated.Key)) added.fileGuid = actualGuid;
                        foreach (var update in looseUpdates.Where(n => n.fileGuid == generated.Key)) update.fileGuid = actualGuid;
                    }
                    foreach (var group in newNodes.Where(n => !n.inSource).GroupBy(n => n.fileGuid))
                    {
                        var file = files.Single(f => f.guid == group.Key);
                        using (var document = new Document(file.path))
                        {
                            foreach (var added in group)
                            {
                                var target = Find(document.scene, added.modelPath);
                                added.expectedGeometry.Transform(GeometryWorld(target).inverse * file.nativeToSdk);
                                SetGeometry(document.scene, target, added.expectedGeometry);
                            }
                            document.Save(file.path);
                        }
                        AssetDatabase.ImportAsset(file.path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        BridgeLog.Write(session, "apply.import", file.path);
                    }
                    RegisterNew(session, files, newNodes);
                }
                catch (Exception failure)
                {
                    BridgeLog.Write(session, "apply.rollback", backupDirectory, failure);
                    foreach (var rollback in rollbackFiles)
                    {
                        var path = rollback.Key;
                        var backup = rollback.Value;
                        if (backup != null)
                        {
                            File.Copy(backup, path, true); File.Copy(backup + ".meta", path + ".meta", true);
                            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        }
                        else AssetDatabase.DeleteAsset(path);
                    }
                    File.Copy(Path.Combine(backupDirectory, "original.prefab"), session.sourcePath, true);
                    AssetDatabase.ImportAsset(session.sourcePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    throw;
                }
                session.looseMeshes = session.looseMeshes.Select(m => looseUpdates.FirstOrDefault(u => u.nodeId == m.nodeId) ?? m).ToList();
                session.fbxFiles = files;
                session.fbxBindings.RemoveAll(b => removed.Contains(b));
                foreach (var binding in session.fbxBindings)
                {
                    if (expandedMaterials.TryGetValue(binding.nodeId, out var slots)) binding.materialIds = slots;
                    if (appliedSignatures.TryGetValue(binding.nodeId, out var appliedSignature)) binding.lastAppliedSignature = appliedSignature;
                    if (appliedGeometrySignatures.TryGetValue(binding.nodeId, out var geometrySignature)) binding.lastAppliedGeometrySignature = geometrySignature;
                    if (materialMappings.TryGetValue(binding.nodeId, out var materialMap)) binding.sourceMaterialSlots = materialMap;
                    var file = session.fbxFiles.Single(f => f.guid == binding.fileGuid);
                    var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
                    var filter = native.GetComponentsInChildren<MeshFilter>(true).First(f => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(f.sharedMesh, out string _, out long id) && id == binding.meshId);
                    var frame = file.nativeToSdk * filter.transform.localToWorldMatrix;
                    binding.prefabToMesh = frame.inverse * binding.meshToSdk * binding.prefabToMesh;
                    binding.meshToSdk = frame;
                }
                foreach (var file in files) { file.expectedHash = Hash(file.path); file.expectedMetaHash = Hash(file.path + ".meta"); file.identities = RegisteredIdentities(session, file); }
                var finalPrefabHash = Hash(session.sourcePath);
                session.lastApplySummary = dirtyFiles.Count == 0 && finalPrefabHash == session.prefabFileHash
                    ? BridgeLocalization.Text("Проверка завершена: новых изменений геометрии и материалов нет.")
                    : BridgeLocalization.Text("Обновление завершено. FBX: ") + dirtyFiles.Count + BridgeLocalization.Text("; новых мешей: ") + newNodes.Count + BridgeLocalization.Text("; удалённых дополнений: ") + removed.Count + BridgeLocalization.Text(". Ссылки Unity проверены, резервная копия сохранена.");
                session.prefabFileHash = finalPrefabHash;
            }
            finally { foreach (var source in sources.Values) source.Dispose(); }
        }

        private static void UpdateMaterials(BridgeSession session, Dictionary<string, List<string>> changes, List<SourceFbxBinding> removed, Dictionary<string, List<SourceMaterialSlot>> materialMappings)
        {
            // Model reimport may reset the renderer to FBX defaults even when
            // bridge material IDs have not changed. Reconcile actual assignments
            // after every import instead of only checking the session's slot list.
            var updates = session.fbxBindings.Where(b => !removed.Contains(b) && changes.ContainsKey(b.nodeId)).ToArray();
            if (updates.Length == 0) return;
            var contents = PrefabUtility.LoadPrefabContents(session.sourcePath);
            try
            {
                var changed = false;
                foreach (var binding in updates)
                {
                    var file = session.fbxFiles.Single(f => f.guid == binding.fileGuid);
                    var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
                    var filter = native.GetComponentsInChildren<MeshFilter>(true).Single(f => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(f.sharedMesh, out string _, out long id) && id == binding.meshId);
                    var instance = MaxBridgeWindow.FindIndexed(contents.transform, binding.instancePath);
                    foreach (var renderer in instance.GetComponentsInChildren<MeshRenderer>(true).Where(r => PrefabUtility.GetCorrespondingObjectFromOriginalSource(r.gameObject) == filter.gameObject || (binding.directReference && r.transform == instance && r.GetComponent<MeshFilter>()?.sharedMesh == filter.sharedMesh)))
                    {
                        var expected = ImportedSlotIds(file, filter, changes[binding.nodeId], materialMappings[binding.nodeId]).Select(id => ResolveMaterial(session, id)).ToArray();
                        if (renderer.sharedMaterials.SequenceEqual(expected)) continue;
                        renderer.sharedMaterials = expected;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                        changed = true;
                    }
                }
                if (!changed) return;
                PrefabUtility.SaveAsPrefabAsset(contents, session.sourcePath, out var success);
                if (!success) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось сохранить материалы после Attach."));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static void RemapMaterialPartitions(BridgeSession session, FbxScene scene, FbxNode node, List<string> logicalSlots, Geometry data, int[] originalFaces, List<SourceMaterialSlot> sourceSlots)
        {
            var pairs = new Dictionary<string, int>();
            var retained = new HashSet<int>();
            for (var face = 0; face < data.slots.Count; face++)
            {
                var id = logicalSlots[data.slots[face]];
                var original = originalFaces[face];
                var key = original + ":" + id;
                if (!pairs.TryGetValue(key, out var index))
                {
                    // Keep every original physical material referenced. Split a
                    // partition only when Max assigns several materials to it.
                    if (retained.Add(original)) index = original;
                    else
                    {
                        var record = session.materials.Single(m => m.id == id);
                        index = sourceSlots.Where(s => s.originalMaterialId == "" && s.materialId == id).Select(s => s.index).DefaultIfEmpty(-1).First();
                        if (index < 0) index = node.AddMaterial(FbxSurfacePhong.Create(scene, record.name + MaxBridgeWindow.MaterialTag + id));
                    }
                    pairs.Add(key, index);
                    var slot = sourceSlots.FirstOrDefault(s => s.index == index);
                    if (slot == null) sourceSlots.Add(new SourceMaterialSlot { index = index, materialId = id, originalMaterialId = "" });
                    else slot.materialId = id;
                }
                data.slots[face] = index;
            }
            BridgeLog.Write(session, "apply.material-partitions", NodePath(node) + "; partitions=" + pairs.Count);
        }

        private static void RemapMaterialSlots(BridgeSession session, FbxScene scene, FbxNode node, List<string> logicalSlots, Geometry data, string sourceGuid = null, List<SourceMaterialSlot> sourceSlots = null)
        {
            // A source node can contain unused slots and preview materials from
            // previous updates. Logical session indices are not FBX slot indices.
            // Keep existing material objects for fileID stability; map each used
            // material by its token and append it only when absent.
            var map = new Dictionary<int, int>();
            foreach (var slot in data.slots.Distinct())
            {
                var id = logicalSlots[slot];
                var record = session.materials.Single(m => m.id == id);
                var index = Enumerable.Range(0, node.GetMaterialCount()).Where(i =>
                    MaxBridgeWindow.Token(node.GetMaterial(i).GetName(), MaxBridgeWindow.MaterialTag) == id).DefaultIfEmpty(-1).First();
                var originalSlot = sourceSlots?.FirstOrDefault(s => s.materialId == id && s.index < node.GetMaterialCount());
                if (originalSlot != null) index = originalSlot.index;
                if (record.guid == sourceGuid)
                {
                    // Embedded Unity materials are real asset references, not
                    // disposable previews. Keep the original FBX material in use
                    // so Unity retains its localID when geometry is rewritten.
                    var originals = Enumerable.Range(0, node.GetMaterialCount()).Where(i => node.GetMaterial(i).GetName() == record.name).ToArray();
                    if (originals.Length != 1) throw new InvalidOperationException(BridgeLocalization.Text("Не найден однозначный встроенный материал FBX: ") + record.name);
                    index = originals[0];
                }
                if (index < 0)
                {
                    index = node.AddMaterial(FbxSurfacePhong.Create(scene, record.name + MaxBridgeWindow.MaterialTag + id));
                }
                map.Add(slot, index);
                if (sourceSlots != null && !sourceSlots.Any(s => s.index == index)) sourceSlots.Add(new SourceMaterialSlot { index = index, materialId = id });
            }
            for (var i = 0; i < data.slots.Count; i++) data.slots[i] = map[data.slots[i]];
            BridgeLog.Write(session, "materials.slot-map", NodePath(node) + "; " + string.Join(", ", map.Select(p => logicalSlots[p.Key] + "=" + p.Value)));
        }

        private static List<string> ImportedSlotIds(SourceFbxFile file, MeshFilter nativeFilter, List<string> logicalSlots, List<SourceMaterialSlot> sourceSlots = null)
        {
            var indices = ImportedSdkSlots(file, nativeFilter);
            if (sourceSlots != null && indices.All(index => sourceSlots.Any(s => s.index == index && logicalSlots.Contains(s.materialId))))
                return indices.Select(index => sourceSlots.First(s => s.index == index && logicalSlots.Contains(s.materialId)).materialId).ToList();
            using (var document = new Document(file.path))
            {
                var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path).GetComponentsInChildren<MeshFilter>(true).First(f => f.sharedMesh == nativeFilter.sharedMesh);
                var node = MatchNative(document.scene, native);
                return indices.Select((index, submesh) =>
                {
                    var mapping = sourceSlots?.FirstOrDefault(s => s.index == index && logicalSlots.Contains(s.materialId));
                    if (mapping != null) return mapping.materialId;
                    var id = index < node.GetMaterialCount() ? MaxBridgeWindow.Token(node.GetMaterial(index).GetName(), MaxBridgeWindow.MaterialTag) : null;
                    if (id != null && logicalSlots.Contains(id)) return id;
                    if (submesh >= logicalSlots.Count) throw new InvalidOperationException(BridgeLocalization.Text("Не хватает исходных слотов материала: ") + nativeFilter.name);
                    return logicalSlots[submesh];
                }).ToList();
            }
        }

        private static List<int> ImportedSdkSlots(SourceFbxFile file, MeshFilter nativeFilter)
        {
            var nativeRoot = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
            var candidates = nativeRoot.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh == nativeFilter.sharedMesh).ToArray();
            nativeFilter = candidates.FirstOrDefault(f => f.name == nativeFilter.name) ?? candidates.First();
            using (var document = new Document(file.path))
            {
                var node = MatchNative(document.scene, nativeFilter);
                var layer = node.GetMesh().GetLayerCount() == 0 ? null : node.GetMesh().GetLayer(0).GetMaterials();
                var counts = new int[Math.Max(1, node.GetMaterialCount())];
                for (var polygon = 0; polygon < node.GetMesh().GetPolygonCount(); polygon++)
                {
                    var slot = layer == null ? 0 : layer.GetIndexArray().GetAt(LayerIndex(layer, polygon, 0, 0));
                    if (slot < 0 || slot >= counts.Length)
                        throw new InvalidOperationException("FBX «" + file.path + BridgeLocalization.Text("», объект «") + node.GetName() + "»: material ID " + (slot + 1) + BridgeLocalization.Text(" вне таблицы из ") + counts.Length + BridgeLocalization.Text(" слотов. Сохрани диагностику и проверь назначения материалов в Max."));
                    counts[slot] += node.GetMesh().GetPolygonSize(polygon) - 2;
                }
                var used = Enumerable.Range(0, counts.Length).Where(i => counts[i] > 0).ToArray();
                if (used.Length != nativeFilter.sharedMesh.subMeshCount)
                    throw new InvalidOperationException(BridgeLocalization.Text("Не совпало число material submesh после импорта: ") + nativeFilter.name);
                var imported = nativeFilter.GetComponent<MeshRenderer>().sharedMaterials;
                var external = ((ModelImporter)AssetImporter.GetAtPath(file.path)).GetExternalObjectMap();
                var result = new List<int>();
                for (var submesh = 0; submesh < used.Length; submesh++)
                {
                    var sdkIndex = used[submesh];
                    var token = submesh < imported.Length && imported[submesh] != null ? MaxBridgeWindow.Token(imported[submesh].name, MaxBridgeWindow.MaterialTag) : null;
                    var materialMatches = used.Where(i =>
                    {
                        if (i >= node.GetMaterialCount()) return false;
                        var name = node.GetMaterial(i).GetName();
                        if (token != null) return MaxBridgeWindow.Token(name, MaxBridgeWindow.MaterialTag) == token;
                        if (submesh >= imported.Length || imported[submesh] == null) return false;
                        return name == imported[submesh].name || external.Any(p => p.Key.name == name && p.Value == imported[submesh]);
                    }).ToArray();
                    if (used.Length == 1) sdkIndex = used[0];
                    else if (materialMatches.Length == 1)
                        sdkIndex = materialMatches[0];
                    else
                        sdkIndex = MatchSubmeshGeometry(file, node, nativeFilter, submesh, layer);
                    result.Add(sdkIndex);
                }
                return result;
            }
        }

        private static int MatchSubmeshGeometry(SourceFbxFile file, FbxNode node, MeshFilter filter, int submesh, FbxLayerElementMaterial materials)
        {
            const float epsilon = .001f;
            var buckets = new Dictionary<Vector3Int, List<Tuple<Vector3, int>>>();
            var mesh = node.GetMesh(); var sdkFrame = GeometryWorld(node);
            for (var polygon = 0; polygon < mesh.GetPolygonCount(); polygon++)
            {
                for (var corner = 0; corner < mesh.GetPolygonSize(polygon); corner++)
                {
                    var point = sdkFrame.MultiplyPoint3x4(Vec(mesh.GetControlPointAt(mesh.GetPolygonVertex(polygon, corner))));
                    var cell = Vector3Int.FloorToInt(point / epsilon);
                    if (!buckets.TryGetValue(cell, out var list)) buckets.Add(cell, list = new List<Tuple<Vector3, int>>());
                    list.Add(Tuple.Create(point, polygon));
                }
            }
            var vertices = filter.sharedMesh.vertices;
            var indices = filter.sharedMesh.GetTriangles(submesh);
            var frame = file.nativeToSdk * filter.transform.localToWorldMatrix;
            HashSet<int> selected = null;
            for (var triangle = 0; triangle < indices.Length; triangle += 3)
            {
                var points = new[] { frame.MultiplyPoint3x4(vertices[indices[triangle]]), frame.MultiplyPoint3x4(vertices[indices[triangle + 1]]), frame.MultiplyPoint3x4(vertices[indices[triangle + 2]]) };
                // Unity can choose either diagonal of a source quad/ngon. Match
                // triangle corners to one source polygon, not to its first three
                // vertices or an assumed triangulation.
                HashSet<int> polygons = null;
                foreach (var point in points)
                {
                    var cell = Vector3Int.FloorToInt(point / epsilon);
                    var candidates = new HashSet<int>();
                    for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
                        if (buckets.TryGetValue(cell + new Vector3Int(x, y, z), out var list))
                            foreach (var candidate in list)
                                if ((point - candidate.Item1).sqrMagnitude <= epsilon * epsilon) candidates.Add(candidate.Item2);
                    if (polygons == null) polygons = candidates; else polygons.IntersectWith(candidates);
                }
                var matches = new HashSet<int>(polygons.Select(p => materials.GetIndexArray().GetAt(LayerIndex(materials, p, 0, 0))));
                if (selected == null) selected = matches; else selected.IntersectWith(matches);
                if (selected.Count == 0)
                    throw new InvalidOperationException(BridgeLocalization.Text("Неоднозначное сопоставление material submesh: ") + filter.name + ", submesh=" + submesh + ", triangle=" + triangle / 3 + ", slots=" + string.Join(",", matches));
            }
            if (selected == null || selected.Count != 1) throw new InvalidOperationException(BridgeLocalization.Text("Неоднозначный material submesh: ") + filter.name + "; submesh=" + submesh + "; slots=" + (selected == null ? "empty" : string.Join(",", selected)));
            var slot = selected.Single();
            BridgeLog.Write(null, "material.geometry-map", file.path + "; node=" + filter.name + "; submesh=" + submesh + "; sdkSlot=" + slot);
            return slot;
        }

        internal static Material ResolveMaterial(BridgeSession session, string id)
        {
            var record = session.materials.Single(m => m.id == id);
            if (string.IsNullOrEmpty(record.guid)) return null;
            return AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(record.guid)).OfType<Material>()
                .Single(m => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string _, out long localId) && localId == record.localId);
        }
        private static void RegisterNew(BridgeSession session, List<SourceFbxFile> files, List<NewNode> additions)
        {
            if (additions.Count == 0) return;
            var contents = PrefabUtility.LoadPrefabContents(session.sourcePath);
            var pending = new List<SourceFbxBinding>();
            try
            {
                var instances = new Dictionary<string, GameObject>();
                foreach (var added in additions)
                {
                    var file = files.Single(f => f.guid == added.fileGuid);
                    var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
                    var nodeName = added.modelPath.Split('/').Last();
                    var nativeFilter = native.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == nodeName);
                    var instanceKey = added.fileGuid + ":" + added.parentInstancePath;
                    if (added.inSource) instances[instanceKey] = MaxBridgeWindow.FindIndexed(contents.transform, added.parentInstancePath).gameObject;
                    if (!instances.TryGetValue(instanceKey, out var instance))
                    {
                        instance = PrefabUtility.InstantiatePrefab(native, contents.transform) as GameObject;
                        if (instance == null) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось добавить FBX-дополнение в prefab."));
                        SetLocalFrame(instance.transform, native.transform.localToWorldMatrix);
                        instances.Add(instanceKey, instance);
                    }
                    MeshFilter target;
                    if (added.directParent)
                    {
                        var child = new GameObject(nodeName);
                        child.transform.SetParent(instance.transform, false);
                        var parentFrame = contents.transform.worldToLocalMatrix * instance.transform.localToWorldMatrix;
                        SetLocalFrame(child.transform, parentFrame.inverse * added.sdkToPrefab * file.nativeToSdk * nativeFilter.transform.localToWorldMatrix);
                        target = child.AddComponent<MeshFilter>();
                        target.sharedMesh = nativeFilter.sharedMesh;
                        child.AddComponent<MeshRenderer>();
                    }
                    else target = instance.GetComponentsInChildren<MeshFilter>(true)
                        .Single(f => PrefabUtility.GetCorrespondingObjectFromOriginalSource(f.gameObject) == nativeFilter.gameObject);
                    var prefabFrame = contents.transform.worldToLocalMatrix * target.transform.localToWorldMatrix;
                    VerifyPositions(added.expectedPositions, target.sharedMesh.vertices.Select(v => prefabFrame.MultiplyPoint3x4(v)));
                    var renderer = target.GetComponent<MeshRenderer>();
                    var parentRenderer = MaxBridgeWindow.FindIndexed(contents.transform, added.parentInstancePath).GetComponent<MeshRenderer>();
                    var parentMaterials = parentRenderer != null ? parentRenderer.sharedMaterials : Array.Empty<Material>();
                    renderer.sharedMaterials = ImportedSlotIds(file, nativeFilter, added.materialIds).Select((id, slot) =>
                    {
                        try { return ResolveMaterial(session, id); }
                        // Imported embedded materials may not have a stable local ID.
                        // The Max material came from the parent FBX, so retain the
                        // parent's assigned material as the safe Unity fallback.
                        catch (InvalidOperationException) { return parentMaterials.ElementAtOrDefault(slot) ?? parentMaterials.FirstOrDefault(); }
                    }).ToArray();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    if (session.meshColliders && target.GetComponent<MeshCollider>() == null)
                        target.gameObject.AddComponent<MeshCollider>().sharedMesh = target.sharedMesh;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(nativeFilter.sharedMesh, out string _, out long meshId);
                    pending.Add(new SourceFbxBinding
                    {
                        nodeId = added.id, fileGuid = added.fileGuid, modelPath = added.modelPath,
                        meshId = meshId, bridgeCreated = true, sourceAddition = added.inSource, directReference = added.directParent, instancePath = IndexedPath(contents.transform, added.directParent ? target.transform : instance.transform),
                        prefabToMesh = (contents.transform.worldToLocalMatrix * target.transform.localToWorldMatrix).inverse,
                        meshToSdk = file.nativeToSdk * nativeFilter.transform.localToWorldMatrix,
                        materialIds = added.materialIds
                    });
                }
                PrefabUtility.SaveAsPrefabAsset(contents, session.sourcePath, out var success);
                if (!success) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось сохранить новые объекты prefab."));
                session.fbxBindings.AddRange(pending);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static void SetLocalFrame(Transform target, Matrix4x4 matrix)
        {
            var scale = new Vector3(((Vector3)matrix.GetColumn(0)).magnitude, ((Vector3)matrix.GetColumn(1)).magnitude, ((Vector3)matrix.GetColumn(2)).magnitude);
            if (matrix.determinant < 0) scale.x = -scale.x;
            var rotation = Quaternion.LookRotation(matrix.GetColumn(2) / scale.z, matrix.GetColumn(1) / scale.y);
            target.localPosition = matrix.GetColumn(3);
            target.localRotation = rotation;
            target.localScale = scale;
            var reconstructed = Matrix4x4.TRS(target.localPosition, rotation, scale);
            for (var i = 0; i < 16; i++)
                if (Mathf.Abs(reconstructed[i] - matrix[i]) > .0001f)
                    throw new InvalidOperationException(BridgeLocalization.Text("Преобразование FBX содержит shear. Синхронизация остановлена."));
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        [MenuItem("Tools/Unity3dsMaxBridge/Repair added mesh coordinates")]
        private static void RepairAddedCoordinates()
        {
            var sessionPath = EditorPrefs.GetString(MaxBridgeWindow.SessionPreference);
            var session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(sessionPath));
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == session.sourcePath && stage.scene.isDirty)
                throw new InvalidOperationException(BridgeLocalization.Text("Сохрани открытый prefab перед исправлением координат."));
            if (Hash(session.sourcePath) != session.prefabFileHash)
                throw new InvalidOperationException(BridgeLocalization.Text("Prefab изменён после экспорта. Исправление остановлено."));
            var additions = session.fbxBindings.Where(b => b.bridgeCreated && !b.sourceAddition).ToArray();
            var files = session.fbxFiles.Where(f => additions.Any(b => b.fileGuid == f.guid)).ToArray();
            var backup = Path.Combine(session.directory, "Backups", "Coordinates_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff"));
            Directory.CreateDirectory(backup);
            File.Copy(session.sourcePath, Path.Combine(backup, "original.prefab"));
            foreach (var file in files)
            {
                if (Hash(file.path) != file.expectedHash) throw new InvalidOperationException(BridgeLocalization.Text("FBX изменён вне bridge: ") + file.path);
                File.Copy(file.path, Path.Combine(backup, file.guid + ".fbx"));
            }
            var expected = new Dictionary<string, Geometry>();
            try
            {
                using (var original = new Document(Path.Combine(session.directory, "ToMax.fbx")))
                using (var baseline = new Document(Path.Combine(session.directory, "MaxBaseline.fbx")))
                using (var edited = new Document(Path.Combine(session.directory, "Edited.fbx")))
                {
                    var a = Tagged(original.scene); var b = Tagged(baseline.scene); var c = Tagged(edited.scene);
                    var reference = session.fbxBindings.First(v => a.ContainsKey(v.nodeId) && b.ContainsKey(v.nodeId));
                    var correction = Calibration(ReadGeometry(a[reference.nodeId], GeometryWorld(a[reference.nodeId]), reference.materialIds),
                        ReadGeometry(b[reference.nodeId], GeometryWorld(b[reference.nodeId]), reference.materialIds));
                    foreach (var file in files)
                    {
                        using (var document = new Document(file.path))
                        {
                            foreach (var binding in additions.Where(v => v.fileGuid == file.guid))
                            {
                                var data = ReadGeometry(c[binding.nodeId], correction * GeometryWorld(c[binding.nodeId]), binding.materialIds);
                                expected.Add(binding.nodeId, data);
                                var target = Find(document.scene, binding.modelPath);
                                var local = ReadGeometry(c[binding.nodeId], correction * GeometryWorld(c[binding.nodeId]), binding.materialIds);
                                local.Transform(GeometryWorld(target).inverse * file.nativeToSdk);
                                SetGeometry(document.scene, target, local);
                            }
                            document.Save(file.path);
                        }
                        AssetDatabase.ImportAsset(file.path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        var actual = Identities(file.path);
                        if (file.identities.Any(v => !IsPreviewMaterial(session, file, v) && !actual.Any(n => n.id == v.id && n.name == v.name && n.type == v.type)))
                            throw new InvalidOperationException(BridgeLocalization.Text("Изменились ссылки companion FBX. Выполнен откат."));
                        using (var document = new Document(file.path))
                            file.nativeToSdk = MatchAxes(document.scene, AssetDatabase.LoadAssetAtPath<GameObject>(file.path), file.path);
                    }
                }
                var contents = PrefabUtility.LoadPrefabContents(session.sourcePath);
                var report = new StringBuilder();
                try
                {
                    foreach (var binding in additions)
                    {
                        var file = files.Single(f => f.guid == binding.fileGuid);
                        var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
                        var nativeFilter = native.GetComponentsInChildren<MeshFilter>(true).Single(f => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(f.sharedMesh, out string _, out long id) && id == binding.meshId);
                        var instance = MaxBridgeWindow.FindIndexed(contents.transform, binding.instancePath);
                        var target = instance.GetComponentsInChildren<MeshFilter>(true).Single(f => PrefabUtility.GetCorrespondingObjectFromOriginalSource(f.gameObject) == nativeFilter.gameObject);
                        instance.SetParent(contents.transform, false);
                        SetLocalFrame(instance, native.transform.localToWorldMatrix);
                        SetLocalFrame(target.transform, native.transform.worldToLocalMatrix * nativeFilter.transform.localToWorldMatrix);
                        var frame = contents.transform.worldToLocalMatrix * target.transform.localToWorldMatrix;
                        VerifyPositions(expected[binding.nodeId].positions, target.sharedMesh.vertices.Select(v => frame.MultiplyPoint3x4(v)));
                        binding.instancePath = IndexedPath(contents.transform, instance);
                        binding.prefabToMesh = frame.inverse;
                        binding.meshToSdk = file.nativeToSdk * nativeFilter.transform.localToWorldMatrix;
                        report.AppendLine(binding.modelPath + ": all vertices within 1 mm of Max export");
                    }
                    PrefabUtility.SaveAsPrefabAsset(contents, session.sourcePath, out var success);
                    if (!success) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось сохранить prefab."));
                }
                finally { PrefabUtility.UnloadPrefabContents(contents); }
                foreach (var file in files) { file.expectedHash = Hash(file.path); file.expectedMetaHash = Hash(file.path + ".meta"); file.identities = RegisteredIdentities(session, file); }
                session.prefabFileHash = Hash(session.sourcePath);
                MaxBridgeWindow.Save(session);
                File.WriteAllText(Path.Combine(session.directory, "coordinate-validation.txt"), report.ToString());
                Debug.Log("Max Bridge: " + report);
            }
            catch
            {
                foreach (var file in files)
                {
                    File.Copy(Path.Combine(backup, file.guid + ".fbx"), file.path, true);
                    AssetDatabase.ImportAsset(file.path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
                File.Copy(Path.Combine(backup, "original.prefab"), session.sourcePath, true);
                AssetDatabase.ImportAsset(session.sourcePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                throw;
            }
        }

        private static void VerifyPositions(IEnumerable<Vector3> expected, IEnumerable<Vector3> actual)
        {
            const float tolerance = .001f;
            var cells = new Dictionary<Vector3Int, List<Vector3>>();
            foreach (var point in actual)
            {
                var cell = Vector3Int.FloorToInt(point / tolerance);
                if (!cells.TryGetValue(cell, out var points)) cells.Add(cell, points = new List<Vector3>());
                points.Add(point);
            }
            foreach (var point in expected)
            {
                var cell = Vector3Int.FloorToInt(point / tolerance);
                var found = false;
                for (var x = -1; x <= 1 && !found; x++) for (var y = -1; y <= 1 && !found; y++) for (var z = -1; z <= 1 && !found; z++)
                    if (cells.TryGetValue(cell + new Vector3Int(x, y, z), out var points))
                        found = points.Any(p => (point - p).sqrMagnitude <= tolerance * tolerance);
                if (!found) throw new InvalidOperationException(BridgeLocalization.Text("Вершины Unity не совпали с Max с точностью 1 мм. Выполнен откат."));
            }
        }

        [MenuItem("Tools/Unity3dsMaxBridge/Rename added FBX files")]
        private static void RenameAddedFiles()
        {
            var session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(EditorPrefs.GetString(MaxBridgeWindow.SessionPreference)));
            var moves = new List<Tuple<SourceFbxFile, string>>();
            try
            {
                foreach (var file in session.fbxFiles.Where(f => Path.GetFileNameWithoutExtension(f.path).EndsWith("_BridgeMeshes", StringComparison.Ordinal)))
                {
                    var bindings = session.fbxBindings.Where(b => b.fileGuid == file.guid).ToArray();
                    if (bindings.Length != 1 || !bindings[0].bridgeCreated || bindings[0].sourceAddition) continue;
                    if (Hash(file.path) != file.expectedHash) throw new InvalidOperationException(BridgeLocalization.Text("FBX изменён вне bridge: ") + file.path);
                    var name = bindings[0].modelPath.Split('/').Last();
                    name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
                    var destination = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(session.sourcePath, null) + "_" + name + ".fbx");
                    var old = file.path;
                    var error = AssetDatabase.MoveAsset(old, destination);
                    if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                    moves.Add(Tuple.Create(file, old));
                    file.path = destination;
                    var identities = Identities(destination);
                    if (!identities.Any(v => v.id == bindings[0].meshId && v.type == typeof(Mesh).FullName)) throw new InvalidOperationException(BridgeLocalization.Text("Изменилась ссылка меша при переименовании."));
                    file.identities = identities;
                    file.expectedHash = Hash(destination); file.expectedMetaHash = Hash(destination + ".meta");
                }
                session.prefabFileHash = Hash(session.sourcePath);
                MaxBridgeWindow.Save(session);
                foreach (var window in Resources.FindObjectsOfTypeAll<MaxBridgeWindow>()) window.ReloadSession();
            }
            catch
            {
                foreach (var move in moves.AsEnumerable().Reverse()) AssetDatabase.MoveAsset(move.Item1.path, move.Item2);
                throw;
            }
        }

        [MenuItem("Tools/Unity3dsMaxBridge/Resume after saving prefab")]
        private static void ResumeSavedPrefab()
        {
            var session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(EditorPrefs.GetString(MaxBridgeWindow.SessionPreference)));
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == session.sourcePath && stage.scene.isDirty)
                throw new InvalidOperationException(BridgeLocalization.Text("Prefab ещё содержит несохранённые правки."));
            var contents = PrefabUtility.LoadPrefabContents(session.sourcePath);
            try
            {
                foreach (var binding in session.fbxBindings)
                {
                    var instance = MaxBridgeWindow.FindIndexed(contents.transform, binding.instancePath);
                    var matches = instance.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null &&
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(f.sharedMesh, out string guid, out long id) && guid == binding.fileGuid && id == binding.meshId).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException(BridgeLocalization.Text("Ссылка на меш изменена: ") + binding.modelPath);
                    var actual = (contents.transform.worldToLocalMatrix * matches[0].transform.localToWorldMatrix).inverse;
                    for (var i = 0; i < 16; i++) if (Mathf.Abs(actual[i] - binding.prefabToMesh[i]) > .0001f)
                        throw new InvalidOperationException(BridgeLocalization.Text("Transform меша изменён в Unity: ") + binding.modelPath);
                }
                foreach (var binding in session.looseMeshes ?? new List<BridgeLooseMesh>())
                {
                    var target = MaxBridgeWindow.FindIndexed(contents.transform, binding.path);
                    if (target == null || target.GetComponent<MeshFilter>() == null)
                        throw new InvalidOperationException(BridgeLocalization.Text("Защищённый объект prefab изменён: ") + binding.name);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            var backup = Path.Combine(session.directory, "Backups", "SavedPrefab_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff"));
            Directory.CreateDirectory(backup);
            File.Copy(session.sourcePath, Path.Combine(backup, "saved.prefab"));
            session.prefabFileHash = Hash(session.sourcePath);
            MaxBridgeWindow.Save(session);
            File.WriteAllText(Path.Combine(session.directory, "apply.request"), "");
            File.WriteAllText(Path.Combine(session.directory, "Edited.ready"), Guid.NewGuid().ToString("N"));
        }

        [MenuItem("Tools/Unity3dsMaxBridge/Write coordinate report")]
        private static void WriteCoordinateReport()
        {
            var session = JsonUtility.FromJson<BridgeSession>(File.ReadAllText(EditorPrefs.GetString(MaxBridgeWindow.SessionPreference)));
            var report = new StringBuilder();
            var expectedAdded = new Dictionary<string, List<Vector3>>();
            var expectedMaterialCounts = new Dictionary<string, int[]>();
            using (var baseline = new Document(Path.Combine(session.directory, "ToMax.fbx")))
            using (var imported = new Document(Path.Combine(session.directory, "MaxBaseline.fbx")))
            using (var edited = new Document(Path.Combine(session.directory, "Edited.fbx")))
            {
                var a = Tagged(baseline.scene); var b = Tagged(imported.scene); var c = Tagged(edited.scene);
                var reference = session.fbxBindings.First(v => a.ContainsKey(v.nodeId) && b.ContainsKey(v.nodeId));
                var maxToPrefab = Calibration(ReadGeometry(a[reference.nodeId], GeometryWorld(a[reference.nodeId]), reference.materialIds), ReadGeometry(b[reference.nodeId], GeometryWorld(b[reference.nodeId]), reference.materialIds));
                foreach (var binding in session.fbxBindings)
                {
                    if (a.ContainsKey(binding.nodeId) && b.ContainsKey(binding.nodeId))
                    {
                        var correction = Calibration(ReadGeometry(a[binding.nodeId], GeometryWorld(a[binding.nodeId]), binding.materialIds), ReadGeometry(b[binding.nodeId], GeometryWorld(b[binding.nodeId]), binding.materialIds));
                        report.AppendLine("Calibration " + binding.modelPath + "\n" + correction.ToString("F6"));
                    }
                    if (c.TryGetValue(binding.nodeId, out var node))
                    {
                        var materialGeometry = ReadGeometry(node, GeometryWorld(node), binding.materialIds);
                        expectedMaterialCounts[binding.nodeId] = Enumerable.Range(0, binding.materialIds.Count).Select(i => materialGeometry.slots.Count(s => s == i)).ToArray();
                        report.AppendLine("MAX MATERIALS " + binding.modelPath + ": " + string.Join(", ", materialGeometry.slots.GroupBy(v => v).Select(g => g.Key + "=" + g.Count())));
                        report.AppendLine("MAX " + binding.modelPath + " " + BoundsOf(ReadGeometry(node, GeometryWorld(node), binding.materialIds).positions).ToString("F6"));
                        if (binding.bridgeCreated) expectedAdded.Add(binding.nodeId, ReadGeometry(node, maxToPrefab * GeometryWorld(node), binding.materialIds).positions);
                    }
                }
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(session.sourcePath);
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var matrix = prefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                report.AppendLine("UNITY " + filter.name + " " + BoundsOf(filter.sharedMesh.vertices.Select(v => matrix.MultiplyPoint3x4(v))).ToString("F6") + "\n" + matrix.ToString("F6"));
                var assigned = filter.GetComponent<MeshRenderer>().sharedMaterials;
                report.AppendLine("UNITY MATERIALS: " + string.Join(", ", Enumerable.Range(0, filter.sharedMesh.subMeshCount).Select(i => i + "=" + filter.sharedMesh.GetIndexCount(i) / 3 + ":" + (i < assigned.Length && assigned[i] != null ? assigned[i].name : "MISSING"))));
                var materialBinding = session.fbxBindings.FirstOrDefault(v => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(filter.sharedMesh, out string guid, out long id) && guid == v.fileGuid && id == v.meshId);
                if (materialBinding != null && expectedMaterialCounts.TryGetValue(materialBinding.nodeId, out var expectedCounts))
                {
                    var slotIds = ImportedSlotIds(session.fbxFiles.Single(f => f.guid == materialBinding.fileGuid), filter, materialBinding.materialIds);
                    if (!assigned.SequenceEqual(slotIds.Select(id => ResolveMaterial(session, id))))
                        throw new InvalidOperationException(BridgeLocalization.Text("Назначения материалов Unity не совпали с Max: ") + filter.name);
                    var actualCounts = Enumerable.Range(0, filter.sharedMesh.subMeshCount).Select(i => (int)filter.sharedMesh.GetIndexCount(i) / 3).ToArray();
                    var compactCounts = slotIds.Select(id => expectedCounts[materialBinding.materialIds.IndexOf(id)]).ToArray();
                    if (!actualCounts.SequenceEqual(compactCounts))
                    {
                        File.WriteAllText(Path.Combine(session.directory, "coordinates.txt"), report.ToString());
                        throw new InvalidOperationException(BridgeLocalization.Text("Распределение полигонов по материалам не совпало: ") + filter.name + "; Max=" + string.Join(",", expectedCounts) + "; Unity=" + string.Join(",", actualCounts));
                    }
                    report.AppendLine("PASS: material assets and polygon counts match Max");
                }
                var sourcePath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                using (var document = new Document(sourcePath))
                {
                    var sdk = MatchNative(document.scene, filter);
                    var layer = sdk.GetMesh().GetLayer(0).GetMaterials();
                    var indices = layer.GetIndexArray();
                    report.AppendLine("FBX MATERIALS: " + string.Join(", ", Enumerable.Range(0, sdk.GetMaterialCount()).Select(i => i + ":" + sdk.GetMaterial(i).GetName())));
                    report.AppendLine("FBX POLYGONS=" + sdk.GetMesh().GetPolygonCount() + ", MATERIAL INDICES=" + indices.GetCount() + ", COUNTS=" + string.Join(", ", Enumerable.Range(0, indices.GetCount()).Select(i => indices.GetAt(i)).GroupBy(i => i).Select(g => g.Key + "=" + g.Count())));
                }
                var vertices = filter.sharedMesh.vertices;
                var normals = filter.sharedMesh.normals;
                var triangles = filter.sharedMesh.triangles;
                var front = 0; var back = 0;
                var normalFrame = matrix.inverse.transpose;
                for (var i = 0; i < triangles.Length; i += 3)
                {
                    var p = matrix.MultiplyPoint3x4(vertices[triangles[i]]);
                    var q = matrix.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                    var r = matrix.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                    var face = Vector3.Cross(q - p, r - p);
                    if (face.sqrMagnitude < 1e-12f || normals.Length != vertices.Length) continue;
                    var normal = normalFrame.MultiplyVector(normals[triangles[i]] + normals[triangles[i + 1]] + normals[triangles[i + 2]]);
                    if (Vector3.Dot(face, normal) >= 0) front++; else back++;
                }
                report.AppendLine("Winding vs normals: aligned=" + front + ", reversed=" + back + ", determinant=" + matrix.determinant);
                var binding = session.fbxBindings.FirstOrDefault(v => v.bridgeCreated && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(filter.sharedMesh, out string guid, out long id) && guid == v.fileGuid && id == v.meshId);
                if (binding != null)
                {
                    VerifyPositions(expectedAdded[binding.nodeId], filter.sharedMesh.vertices.Select(v => matrix.MultiplyPoint3x4(v)));
                    report.AppendLine("PASS: all vertices within 1 mm");
                }
            }
            File.WriteAllText(Path.Combine(session.directory, "coordinates.txt"), report.ToString());
        }

        internal static string Diagnose(BridgeSession session, string fileName = "Edited.fbx")
        {
            var report = new StringBuilder();
            using (var original = new Document(Path.Combine(session.directory, "ToMax.fbx")))
            using (var edited = new Document(Path.Combine(session.directory, fileName)))
            {
                var a = Tagged(original.scene); var b = Tagged(edited.scene);
                foreach (var binding in session.fbxBindings.Where(v => a.ContainsKey(v.nodeId)))
                {
                    var before = ReadGeometry(a[binding.nodeId], binding.prefabToMesh * GeometryWorld(a[binding.nodeId]), binding.materialIds);
                    var after = ReadGeometry(b[binding.nodeId], binding.prefabToMesh * GeometryWorld(b[binding.nodeId]), binding.materialIds);
                    report.AppendLine(binding.modelPath + " / " + b[binding.nodeId].GetName());
                    report.AppendLine("Vertices " + before.positions.Count + " => " + after.positions.Count + "; equivalent=" + before.Equivalent(after));
                    report.AppendLine("UV counts " + string.Join(",", before.uv.Select(u => u.Count)) + " => " + string.Join(",", after.uv.Select(u => u.Count)));
                    var positionError = 0f; var normalError = 0f;
                    for (var i = 0; i < Math.Min(before.positions.Count, after.positions.Count); i++)
                    { positionError = Mathf.Max(positionError, Vector3.Distance(before.positions[i], after.positions[i])); normalError = Mathf.Max(normalError, Vector3.Distance(before.normals[i], after.normals[i])); }
                    report.AppendLine("Index-aligned position error=" + positionError + "; normal error=" + normalError);
                    report.AppendLine("Colors " + before.colors.Count + " => " + after.colors.Count);
                }
                foreach (var group in session.fbxBindings.GroupBy(v => v.fileGuid + ":" + v.meshId).Where(g => g.Count() > 1))
                {
                    var bindings = group.ToArray();
                    var one = ReadGeometry(b[bindings[0].nodeId], bindings[0].prefabToMesh * GeometryWorld(b[bindings[0].nodeId]), bindings[0].materialIds);
                    var two = ReadGeometry(b[bindings[1].nodeId], bindings[1].prefabToMesh * GeometryWorld(b[bindings[1].nodeId]), bindings[1].materialIds);
                    report.AppendLine("Shared instances equivalent=" + one.Equivalent(two));
                    report.AppendLine("Pair position=" + one.positions.Select((p, i) => Vector3.Distance(p, two.positions[i])).Max() + "; normals=" + one.normals.Select((p, i) => Vector3.Distance(p, two.normals[i])).Max());
                    for (var channel = 0; channel < 8; channel++) if (one.uv[channel].Count != 0)
                        report.AppendLine("Pair UV" + channel + "=" + one.uv[channel].Select((p, i) => Vector2.Distance(p, two.uv[channel][i])).Max());
                }
            }
            return report.ToString();
        }
    }
}
