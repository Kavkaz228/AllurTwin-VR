using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace VRWorkshop.Editor
{
    // Reimport only the three package templates affected by a long project path.
    public static class WorkshopShortPathVerification
    {
        public static void PrepareScene()
        {
            // Switching an existing project cache to its short alias can retain
            // script importer mappings from the former absolute path. Refresh
            // only this application's scripts, then regenerate its authored scene.
            foreach (string guid in AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/VRWorkshop" }))
                AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid), ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/VRWorkshop/Runtime/WorkshopClient.cs");
            if (script == null || script.GetClass() != typeof(WorkshopClient))
                throw new InvalidOperationException("WorkshopClient MonoScript does not resolve to its compiled runtime type.");
            WorkshopBuild.CreateScene();
            var client = UnityEngine.Object.FindFirstObjectByType<WorkshopClient>();
            if (client == null || MonoScript.FromMonoBehaviour(client) != script)
                throw new InvalidOperationException("The rebuilt workshop scene has an invalid runtime script reference.");
            Debug.Log("Short-path runtime script mappings and rebuilt scene verified.");
        }
        [Serializable] sealed class Report
        {
            public bool success;
            public string projectPath, finishedUtc, error;
            public string[] assets;
            public bool[] loaded;
        }

        public static void Reimport()
        {
            const string folder = "Packages/com.unity.inputsystem/InputSystem/Editor/UITKAssetEditor/PackageResources/";
            var report = new Report
            {
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                assets = new[] { folder + "CompositeBindingPropertiesEditor.uxml", folder + "NameAndParameterListViewItemTemplate.uxml", folder + "CompositePartBindingPropertiesEditor.uxml" },
                loaded = new bool[3]
            };
            var expected = new[] { "composite-type-dropdown", "Foldout", "composite-part-dropdown" };
            try
            {
                for (int index = 0; index < report.assets.Length; index++)
                {
                    AssetDatabase.ImportAsset(report.assets[index], ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(report.assets[index]);
                    if (asset == null) throw new InvalidOperationException("Import did not produce a VisualTreeAsset: " + report.assets[index]);
                    var tree = asset.CloneTree();
                    report.loaded[index] = tree.Q(expected[index]) != null;
                    if (!report.loaded[index]) throw new InvalidOperationException("Imported template is missing its actual editor control: " + expected[index]);
                }
                report.success = true;
                Debug.Log("AllurTwin short-path package imports verified: three UXML templates load and instantiate their expected controls.");
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            finally
            {
                report.finishedUtc = DateTime.UtcNow.ToString("O");
                string logs = Path.Combine(report.projectPath, "Logs"); Directory.CreateDirectory(logs);
                File.WriteAllText(Path.Combine(logs, "short-path-verification.json"), JsonUtility.ToJson(report, true));
            }
            if (!report.success) throw new InvalidOperationException(report.error);
        }
    }
}
