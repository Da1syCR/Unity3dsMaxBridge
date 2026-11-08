using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity3dsMaxBridge.Editor
{
    internal static class UniquePrefabCopy
    {
        internal static GameObject Create(string sourcePath)
        {
            if (!sourcePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(BridgeLocalization.Text("Выбери prefab asset."));
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null) throw new InvalidOperationException(BridgeLocalization.Text("Prefab не найден: ") + sourcePath);
            if (source.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
                throw new InvalidOperationException(BridgeLocalization.Text("Bridge поддерживает только статические меши."));
            var parent = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
            var folder = AssetDatabase.GenerateUniqueAssetPath(parent + "/" + source.name + "_Unique");
            var folderGuid = AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            if (string.IsNullOrEmpty(folderGuid)) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось создать папку копии."));
            folder = AssetDatabase.GUIDToAssetPath(folderGuid);
            var destination = folder + "/" + Path.GetFileName(folder) + ".prefab";
            GameObject contents = null;
            try
            {
                AssetDatabase.CreateFolder(folder, "Models");
                var replacements = new Dictionary<Object, Object>();
                foreach (var fbx in AssetDatabase.GetDependencies(sourcePath, true).Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)))
                {
                    var copyPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/Models/" + Path.GetFileName(fbx));
                    if (!AssetDatabase.CopyAsset(fbx, copyPath)) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось скопировать FBX: ") + fbx);
                    AssetDatabase.ImportAsset(copyPath, ImportAssetOptions.ForceSynchronousImport);
                    var copied = AssetDatabase.LoadAllAssetsAtPath(copyPath);
                    foreach (var original in AssetDatabase.LoadAllAssetsAtPath(fbx).Where(a => a != null && !(a is Material) && !(a is Texture)))
                    {
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original, out string _, out long id);
                        var matches = copied.Where(a => a != null && a.GetType() == original.GetType() &&
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string _, out long otherId) && otherId == id).ToArray();
                        if (matches.Length != 1) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось сопоставить объект копии FBX: ") + original.name);
                        replacements.Add(original, matches[0]);
                    }
                }
                contents = PrefabUtility.LoadPrefabContents(sourcePath);
                // Unpack the temporary contents only. This also isolates variants
                // and nested prefab geometry while retaining all overrides and
                // internal component references in the new independent prefab.
                while (true)
                {
                    var instance = contents.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(t => PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject));
                    if (instance == null) break;
                    PrefabUtility.UnpackPrefabInstance(instance.gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                }
                foreach (var component in contents.GetComponentsInChildren<Component>(true).Where(c => c != null))
                {
                    var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue == null) continue;
                        var original = property.objectReferenceValue;
                        if (original is Material || original is Texture) continue;
                        if (!replacements.TryGetValue(original, out var replacement) && original is Mesh mesh && AssetDatabase.GetAssetPath(mesh).EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                        {
                            replacement = Object.Instantiate(mesh);
                            replacement.name = mesh.name;
                            var meshPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/Models/" + SafeName(mesh.name) + ".asset");
                            AssetDatabase.CreateAsset(replacement, meshPath);
                            replacements.Add(original, replacement);
                        }
                        if (replacement != null) property.objectReferenceValue = replacement;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                contents.name = Path.GetFileNameWithoutExtension(destination);
                var result = PrefabUtility.SaveAsPrefabAsset(contents, destination, out var success);
                if (!success || result == null) throw new InvalidOperationException(BridgeLocalization.Text("Не удалось сохранить уникальный prefab."));
                // No copied mesh may still reference an artist source FBX.
                foreach (var mesh in result.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh)
                    .Concat(result.GetComponentsInChildren<MeshCollider>(true).Select(c => c.sharedMesh)).Where(m => m != null))
                {
                    var path = AssetDatabase.GetAssetPath(mesh);
                    if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && !path.StartsWith(folder + "/", StringComparison.Ordinal))
                        throw new InvalidOperationException(BridgeLocalization.Text("Меш остался связан с исходным FBX: ") + mesh.name);
                }
                return result;
            }
            catch
            {
                // Only the folder created by this operation is eligible for cleanup.
                if (AssetDatabase.GUIDToAssetPath(folderGuid) == folder) AssetDatabase.DeleteAsset(folder);
                throw;
            }
            finally { if (contents != null) PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static string SafeName(string name) => new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
    }
}
