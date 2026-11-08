using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;
namespace Unity3dsMaxBridge.Editor
{
    [Serializable] internal sealed class BridgeLooseMesh
    {
        public string nodeId, path, name, fileGuid, modelPath, lastAppliedSignature;
        public long objectId;
        public Matrix4x4 prefabToMesh, meshToSdk;
        public List<string> materialIds = new List<string>();
    }

    internal static partial class SourceFbxBridge
    {
        private static void CaptureLoose(BridgeSession session, BridgeNode entry, Transform root, Transform target, SourceFbxFile file, FbxNode sdk, MeshFilter native)
        {
            var assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(session.sourcePath);
            var persistent = MaxBridgeWindow.FindIndexed(assetRoot.transform, entry.path).gameObject;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(persistent, out string _, out long objectId);
            var renderer = target.GetComponent<MeshRenderer>();
            session.looseMeshes.Add(new BridgeLooseMesh
            {
                nodeId = entry.id, path = entry.path, name = entry.name, objectId = objectId,
                fileGuid = file == null ? "" : file.guid, modelPath = sdk == null ? "" : NodePath(sdk),
                prefabToMesh = (root.worldToLocalMatrix * target.localToWorldMatrix).inverse,
                meshToSdk = file == null ? Matrix4x4.identity : file.nativeToSdk * native.transform.localToWorldMatrix,
                materialIds = MaxBridgeWindow.MaterialSlots(renderer, target.GetComponent<MeshFilter>().sharedMesh).Select((m, slot) => Id(m, session, slot)).ToList()
            });
        }

        private static void PrepareLoose(BridgeSession session, Dictionary<string, FbxNode> remote, Dictionary<string, FbxNode> initial,
            Dictionary<string, FbxNode> initialMax, List<SourceFbxFile> files, Dictionary<string, Document> sources, List<BridgeLooseMesh> updates,
            Dictionary<string, Geometry> expected)
        {
            SourceFbxFile generated = null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(session.sourcePath);
            foreach (var saved in session.looseMeshes)
            {
                if (!remote.TryGetValue(saved.nodeId, out var node) || !initial.TryGetValue(saved.nodeId, out var original) || !initialMax.TryGetValue(saved.nodeId, out var imported))
                    throw new InvalidOperationException(BridgeLocalization.Text("Удалён защищённый объект «") + saved.name + BridgeLocalization.Text("». Разморозь его для редактирования, но сохрани сам объект."));
                var slots = new List<string>(saved.materialIds);
                for (var i = 0; i < node.GetMaterialCount(); i++)
                {
                    var id = MaxBridgeWindow.Token(node.GetMaterial(i).GetName(), MaxBridgeWindow.MaterialTag);
                    if (id != null && !slots.Contains(id) && session.materials.Any(m => m.id == id)) slots.Add(id);
                }
                var target = prefab.GetComponentsInChildren<Transform>(true).Single(t =>
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(t.gameObject, out string _, out long id) && id == saved.objectId);
                // The mesh is assigned directly to this MeshFilter: its vertices
                // must stay in the existing object's local frame. Recover that
                // frame for sessions written by older bridge versions as well.
                var prefabToMesh = (prefab.transform.worldToLocalMatrix * target.localToWorldMatrix).inverse;
                var originalGeometry = ReadGeometry(original, prefabToMesh * GeometryWorld(original), saved.materialIds);
                var importedGeometry = ReadGeometry(imported, prefabToMesh * GeometryWorld(imported), saved.materialIds);
                var data = ReadGeometry(node, prefabToMesh * GeometryWorld(node), slots);
                // A protected mesh can already use a companion FBX.  It must be
                // left untouched until Max geometry actually changes; rewriting
                // an unrelated source FBX may make Unity regenerate fileIDs.
                var signature = data.Signature();
                if (string.IsNullOrEmpty(saved.lastAppliedSignature)
                    ? data.Equivalent(importedGeometry)
                    : signature == saved.lastAppliedSignature) continue;
                data.Transform(Calibration(originalGeometry, importedGeometry));
                var update = JsonUtility.FromJson<BridgeLooseMesh>(JsonUtility.ToJson(saved));
                update.lastAppliedSignature = signature;
                update.prefabToMesh = prefabToMesh;
                expected.Add(saved.nodeId, ReadGeometry(node, prefabToMesh * GeometryWorld(node), slots));
                expected[saved.nodeId].Transform(Calibration(originalGeometry, importedGeometry));
                SourceFbxFile file;
                Document document;
                FbxNode destination;
                if (string.IsNullOrEmpty(saved.fileGuid))
                {
                    if (generated == null)
                    {
                        // Reuse a companion already referenced by this prefab, otherwise
                        // create one model only when a protected mesh is actually edited.
                        var stem = Path.ChangeExtension(session.sourcePath, null) + "_BridgeMeshes";
                        generated = files.FirstOrDefault(f => Path.ChangeExtension(f.path, null) == stem);
                        if (generated == null)
                        {
                            var path = AssetDatabase.GenerateUniqueAssetPath(stem + ".fbx");
                            generated = new SourceFbxFile { guid = Guid.NewGuid().ToString("N"), path = path, expectedHash = "", expectedMetaHash = "", nativeToSdk = Matrix4x4.identity };
                            files.Add(generated);
                            var fresh = new Document();
                            fresh.scene.GetRootNode().AddChild(FbxNode.Create(fresh.scene, "BridgeMeshes"));
                            sources.Add(generated.guid, fresh);
                        }
                    }
                    file = generated; document = sources[file.guid];
                    var name = saved.name;
                    if (Nodes(document.scene.GetRootNode()).Any(n => n.GetName() == name)) name += "_" + saved.nodeId.Substring(0, 8);
                    destination = FbxNode.Create(document.scene, name);
                    document.scene.GetRootNode().GetChild(0).AddChild(destination);
                    update.fileGuid = file.guid; update.modelPath = NodePath(destination);
                    update.meshToSdk = Matrix4x4.identity;
                }
                else
                {
                    file = files.Single(f => f.guid == saved.fileGuid); document = sources[file.guid];
                    destination = Find(document.scene, saved.modelPath);
                }
                data.Transform(GeometryWorld(destination).inverse * update.meshToSdk);
                RemapMaterialSlots(session, document.scene, destination, slots, data, file.guid);
                SetGeometry(document.scene, destination, data);
                update.materialIds = slots;
                updates.Add(update);
            }
        }

        private static void RegisterLoose(BridgeSession session, List<SourceFbxFile> files, List<BridgeLooseMesh> updates,
            Dictionary<string, Geometry> expected)
        {
            if (updates.Count == 0) return;
            foreach (var file in files.Where(f => string.IsNullOrEmpty(f.expectedHash)))
            {
                var previous = file.guid;
                file.guid = AssetDatabase.AssetPathToGUID(file.path);
                using (var normalized = new Document(file.path))
                    file.nativeToSdk = MatchAxes(normalized.scene, AssetDatabase.LoadAssetAtPath<GameObject>(file.path), file.path);
                foreach (var update in updates.Where(u => u.fileGuid == previous)) update.fileGuid = file.guid;
            }
            // A first import reveals Unity's native root/axis conversion. Bake
            // its inverse into promoted geometry before assigning the raw Mesh;
            // the FBX root transform is not part of the existing prefab object.
            foreach (var group in updates.Where(u => string.IsNullOrEmpty(session.looseMeshes.Single(s => s.nodeId == u.nodeId).fileGuid)).GroupBy(u => u.fileGuid))
            {
                var file = files.Single(f => f.guid == group.Key);
                var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
                using (var document = new Document(file.path))
                {
                    foreach (var update in group)
                    {
                        var nativeFilter = native.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == update.modelPath.Split('/').Last());
                        var frame = file.nativeToSdk * nativeFilter.transform.localToWorldMatrix;
                        var destination = Find(document.scene, update.modelPath);
                        var data = ReadGeometry(destination, update.meshToSdk.inverse * GeometryWorld(destination), update.materialIds);
                        data.Transform(GeometryWorld(destination).inverse * frame);
                        // ReadGeometry returns logical session slots. Sparse/empty
                        // Max IDs need mapping back to this FBX's physical slots.
                        RemapMaterialSlots(session, document.scene, destination, update.materialIds, data, file.guid);
                        SetGeometry(document.scene, destination, data);
                        update.meshToSdk = frame;
                    }
                    document.Save(file.path);
                }
                AssetDatabase.ImportAsset(file.path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var actual = Identities(file.path);
                foreach (var identity in file.identities)
                    if (!IsPreviewMaterial(session, file, identity) && !actual.Any(a => a.id == identity.id && a.type == identity.type && a.name == identity.name))
                        throw new InvalidOperationException(BridgeLocalization.Text("Изменилась ссылка Unity при подготовке меша: ") + identity.name);
                BridgeLog.Write(session, "loose.bake-frame", file.path);
            }
            var contents = PrefabUtility.LoadPrefabContents(session.sourcePath);
            try
            {
                foreach (var update in updates)
                {
                    var persistentRoot = AssetDatabase.LoadAssetAtPath<GameObject>(session.sourcePath);
                    var persistent = persistentRoot.GetComponentsInChildren<Transform>(true).FirstOrDefault(t =>
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(t.gameObject, out string _, out long id) && id == update.objectId);
                    if (persistent == null) throw new InvalidOperationException(BridgeLocalization.Text("Не найден исходный объект prefab: ") + update.name);
                    var target = MaxBridgeWindow.FindIndexed(contents.transform, IndexedPath(persistentRoot.transform, persistent));
                    var filter = target.GetComponent<MeshFilter>();
                    var file = files.Single(f => f.guid == update.fileGuid);
                    var native = AssetDatabase.LoadAssetAtPath<GameObject>(file.path);
                    var nativeFilter = native.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == update.modelPath.Split('/').Last());
                    VerifyPositions(expected[update.nodeId].positions, nativeFilter.sharedMesh.vertices);
                    var oldMesh = filter.sharedMesh;
                    filter.sharedMesh = nativeFilter.sharedMesh;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                    foreach (var collider in target.GetComponents<MeshCollider>().Where(c => c.sharedMesh == oldMesh))
                    {
                        collider.sharedMesh = nativeFilter.sharedMesh;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
                    }
                    var renderer = target.GetComponent<MeshRenderer>();
                    renderer.sharedMaterials = ImportedSlotIds(file, nativeFilter, update.materialIds).Select(id => ResolveMaterial(session, id)).ToArray();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    var frame = file.nativeToSdk * nativeFilter.transform.localToWorldMatrix;
                    update.meshToSdk = frame;
                    BridgeLog.Write(session, "loose.verified", update.name + "; raw mesh vertices match within 1 mm; prefab transform preserved");
                }
                PrefabUtility.SaveAsPrefabAsset(contents, session.sourcePath, out var success);
                if (!success) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось сохранить отредактированные меши prefab."));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
    }
}
