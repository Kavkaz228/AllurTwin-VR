using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRWorkshop.Editor
{
    public static class WorkshopIndustrialAssets
    {
        public static void PersistScene()
        {
            const string directory = "Assets/VRWorkshop/Resources/Industrial";
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
            // Also save materials used only by robots which are instantiated after entering Play.
            foreach (WorkshopSurfaceLibrary.Surface surface in System.Enum.GetValues(typeof(WorkshopSurfaceLibrary.Surface)))
                SaveMaterial(WorkshopSurfaceLibrary.Get(surface, false), directory);
            foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] != null && materials[i].name.StartsWith("Industrial-")) materials[i] = SaveMaterial(materials[i], directory);
                renderer.sharedMaterials = materials;
            }
            foreach (var collider in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                var material = collider.sharedMaterial;
                if (material != null && !AssetDatabase.Contains(material))
                    collider.sharedMaterial = Save(material, directory + "/" + material.name + ".physicMaterial");
            }
            foreach (var filter in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
                if (filter.sharedMesh != null && filter.sharedMesh.name.StartsWith("Industrial-"))
                {
                    filter.sharedMesh = Save(filter.sharedMesh, directory + "/" + filter.sharedMesh.name + ".asset");
                    var contact = filter.GetComponent<MeshCollider>();
                    if (contact != null) contact.sharedMesh = filter.sharedMesh;
                }
            AssetDatabase.SaveAssets();
        }
        static Material SaveMaterial(Material material, string directory)
        {
            foreach (string property in new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap" })
            {
                var texture = material.GetTexture(property) as Texture2D;
                if (texture != null && !AssetDatabase.Contains(texture))
                    material.SetTexture(property, Save(texture, directory + "/" + texture.name + ".asset"));
            }
            return Save(material, directory + "/" + material.name + ".mat");
        }
        static T Save<T>(T asset, string path) where T : Object
        {
            if (AssetDatabase.Contains(asset)) return asset;
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null) { AssetDatabase.CreateAsset(asset, path); return asset; }
            EditorUtility.CopySerialized(asset, existing);
            EditorUtility.SetDirty(existing);
            return existing;
        }
    }
}
