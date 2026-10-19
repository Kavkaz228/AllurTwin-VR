using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace VRWorkshop.Editor
{
    public static class WorkshopBuild
    {
        public const string ScenePath = "Assets/VRWorkshop/Scenes/Цех.unity";
        [MenuItem("VR Workshop/1. Create workshop scene (Цех)")]
        public static void CreateScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory("Assets/VRWorkshop/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            WorkshopScannedMaterials.Prepare();
            WorkshopEnvironment.BuildLayout();
            WorkshopIndustrialAssets.PersistScene();
            new GameObject("VR Workshop application").AddComponent<WorkshopClient>();
            PersistSceneMaterials();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            ConfigureCommon();
            AssetDatabase.SaveAssets();
            Debug.Log("Application scene created. Press Play for the desktop test client, or configure an OpenXR target.");
        }
        static void PersistSceneMaterials()
        {
            const string directory = "Assets/VRWorkshop/Materials";
            Directory.CreateDirectory(directory);
            int index = 0;
            var persisted = new System.Collections.Generic.Dictionary<Material, Material>();
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; ++i)
                {
                    Material material = materials[i];
                    if (material == null || AssetDatabase.Contains(material)) continue;
                    if (persisted.TryGetValue(material, out var saved)) { materials[i] = saved; continue; }
                    string path = directory + "/Workshop-" + (++index) + ".mat";
                    var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (existing != null) { EditorUtility.CopySerialized(material, existing); materials[i] = existing; }
                    else AssetDatabase.CreateAsset(material, path);
                    persisted[material] = materials[i];
                }
                renderer.sharedMaterials = materials;
            }
            AssetDatabase.SaveAssets();
        }
        static void ConfigureCommon()
        {
            PlayerSettings.companyName = "AllurTwin Lab";
            PlayerSettings.productName = "AllurTwin VR";
            PlayerSettings.runInBackground = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            var player = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var input = player.FindProperty("activeInputHandler");
            if (input != null) { input.intValue = 1; player.ApplyModifiedPropertiesWithoutUndo(); }
            // Runtime creates these built-in materials; keep shaders when no material asset references them.
            var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var shaders = graphics.FindProperty("m_AlwaysIncludedShaders");
            foreach (string name in new[] { "Standard", "Sprites/Default", "VRWorkshop/BodyCoating" })
            {
                Shader shader = Shader.Find(name);
                bool found = false;
                for (int i = 0; i < shaders.arraySize; ++i)
                    if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader) found = true;
                if (!found && shader != null)
                {
                    int at = shaders.arraySize;
                    shaders.InsertArrayElementAtIndex(at);
                    shaders.GetArrayElementAtIndex(at).objectReferenceValue = shader;
                }
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }
        [MenuItem("VR Workshop/2. Configure Windows OpenXR")]
        public static void ConfigureWindows()
        {
            ConfigureCommon();
            ConfigureXR(BuildTargetGroup.Standalone);
            AssetDatabase.SaveAssets();
            Debug.Log("Windows OpenXR configured for HTC VIVE Pro 2 and VIVE wand controllers. Set SteamVR as the active OpenXR runtime and validate Project Settings > XR Plug-in Management > OpenXR.");
        }
        [MenuItem("VR Workshop/3. Configure Android OpenXR")]
        public static void ConfigureAndroid()
        {
            ConfigureCommon();
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "org.allurtwin.vr");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            ConfigureXR(BuildTargetGroup.Android);
            AssetDatabase.SaveAssets();
            Debug.Log("Android ARM64 OpenXR configured. Enable the target headset feature group (for Quest: Meta Quest Support) and fix all OpenXR validation errors before building.");
        }
        static void ConfigureXR(BuildTargetGroup target)
        {
            Directory.CreateDirectory("Assets/XR/Settings");
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget))
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/Settings/WorkshopXRSettings.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }
            var general = perTarget.SettingsForBuildTarget(target);
            if (general == null)
            {
                general = ScriptableObject.CreateInstance<XRGeneralSettings>();
                general.name = target + " XR General Settings";
                AssetDatabase.AddObjectToAsset(general, perTarget);
                perTarget.SetSettingsForBuildTarget(target, general);
            }
            if (general.Manager == null)
            {
                general.Manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                general.Manager.name = target + " XR Manager";
                AssetDatabase.AddObjectToAsset(general.Manager, perTarget);
            }
            general.InitManagerOnStart = true;
            general.Manager.automaticLoading = true;
            general.Manager.automaticRunning = true;
            if (!XRPackageMetadataStore.AssignLoader(general.Manager, typeof(OpenXRLoader).FullName, target))
                throw new InvalidOperationException("Could not assign OpenXR loader. Open XR Plug-in Management and enable OpenXR for " + target);
            var openXR = OpenXRSettings.GetSettingsForBuildTargetGroup(target);
            if (openXR != null)
            {
                // Windows deployment uses VIVE Pro 2 with the original VIVE wand controllers.
                // Keep the Touch profile for the separate Android configuration.
                var touch = openXR.GetFeature<OculusTouchControllerProfile>();
                if (touch != null) { touch.enabled = target != BuildTargetGroup.Standalone; EditorUtility.SetDirty(touch); }
                if (target == BuildTargetGroup.Standalone)
                {
                    var vive = openXR.GetFeature<HTCViveControllerProfile>();
                    if (vive == null)
                        throw new InvalidOperationException("HTC Vive Controller Profile is unavailable in the OpenXR Windows settings.");
                    vive.enabled = true;
                    EditorUtility.SetDirty(vive);
                }
                EditorUtility.SetDirty(openXR);
            }
            EditorUtility.SetDirty(general.Manager);
            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(perTarget);
        }
        [MenuItem("VR Workshop/Build Windows player")]
        public static void BuildWindows()
        {
            EnsureScene(); ConfigureWindows();
            Build(BuildTarget.StandaloneWindows64, "Builds/Windows/AllurTwinVR.exe");
        }
        public static void RebuildWindowsScene()
        {
            CreateScene();
            BuildWindows();
        }
        [MenuItem("VR Workshop/Build Android APK")]
        public static void BuildAndroid()
        {
            EnsureScene(); ConfigureAndroid();
            Build(BuildTarget.Android, "Builds/Android/AllurTwinVR.apk");
        }
        static void EnsureScene() { if (!File.Exists(ScenePath)) CreateScene(); }
        static void Build(BuildTarget target, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = path, target = target, options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Build failed: " + report.summary.result + ". Read Console/OpenXR project validation.");
            Debug.Log("Built " + Path.GetFullPath(path));
        }
    }
}
