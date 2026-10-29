#if UNITY_EDITOR
using System;
using System.IO;
using HakoLab.ARBloom;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;

namespace HakoLab.ARBloom.Editor
{
    [InitializeOnLoad]
    public static class ARBloomProjectSetup
    {
        private const string ScenePath = "Assets/Scenes/ARBloom.unity";
        private const string MarkerPath = "Assets/ARBloom/.setup-v2";
        private const string LoaderType = "UnityEngine.XR.ARCore.ARCoreLoader";

        static ARBloomProjectSetup()
        {
            EditorApplication.delayCall += AutoSetup;
        }

        [MenuItem("AR Bloom/1. Setup or Repair Project")]
        public static void ManualSetup()
        {
            SetupProject(true);
        }

        [MenuItem("AR Bloom/2. Build & Run Android")]
        public static void BuildAndRunAndroid()
        {
            SetupProject(false);

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                EditorUtility.DisplayDialog(
                    "Android module belum ada",
                    "Install Android Build Support + Android SDK & NDK Tools + OpenJDK dari Unity Hub dulu.",
                    "OK");
                return;
            }

            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                EditorUtility.DisplayDialog("Gagal switch Android", "Coba switch ke Android dari Build Profiles lalu ulangi.", "OK");
                return;
            }

            Directory.CreateDirectory("Builds/Android");
            EditorUserBuildSettings.buildAppBundle = false;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/Android/ARBloom.apk",
                target = BuildTarget.Android,
                options = BuildOptions.Development | BuildOptions.AutoRunPlayer
            };

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"[AR Bloom] Build sukses: {options.locationPathName}");
            }
            else
            {
                Debug.LogError($"[AR Bloom] Build gagal: {report.summary.result}. Cek Console Unity.");
            }
        }

        private static void AutoSetup()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += AutoSetup;
                return;
            }

            if (!File.Exists(MarkerPath) || !File.Exists(ScenePath))
                SetupProject(false);
        }

        private static void SetupProject(bool showDialog)
        {
            ConfigurePlayerSettings();
            ConfigureXR();
            EnsureScene();

            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath) ?? "Assets/ARBloom");
            File.WriteAllText(MarkerPath,
                "Auto-generated project setup marker. Delete this file to force setup on next Unity open.\n");
            AssetDatabase.Refresh();
            AssetDatabase.SaveAssets();

            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "AR Bloom siap",
                    "Project sudah dikonfigurasi. Sambungkan HP + aktifkan USB debugging, lalu pilih AR Bloom > 2. Build & Run Android.",
                    "Gas");
            }
        }

        private static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = "Hako Lab";
            PlayerSettings.productName = "AR Bloom";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.optimizedFramePacing = true;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.hakolab.arbloom");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });

            // Force old Input Manager so Input.touchCount works without any package/input-action setup.
            // Unity stores this in ProjectSettings as "activeInputHandler": 0 = Input Manager.
            try
            {
                UnityEngine.Object[] settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
                if (settings != null && settings.Length > 0)
                {
                    var so = new SerializedObject(settings[0]);
                    SerializedProperty prop = so.FindProperty("activeInputHandler");
                    if (prop != null)
                    {
                        prop.intValue = 0;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AR Bloom] Tidak bisa memaksa Active Input Handling: " + ex.Message);
            }
        }

        private static void ConfigureXR()
        {
            try
            {
                XRGeneralSettingsPerBuildTarget perBuildTarget = null;
                EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out perBuildTarget);

                if (perBuildTarget == null)
                {
                    Directory.CreateDirectory("Assets/XR");
                    const string settingsAssetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
                    perBuildTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(settingsAssetPath);

                    if (perBuildTarget == null)
                    {
                        perBuildTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                        AssetDatabase.CreateAsset(perBuildTarget, settingsAssetPath);
                        AssetDatabase.SaveAssets();
                    }

                    EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perBuildTarget, true);
                }

                XRGeneralSettings general = perBuildTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
                if (general == null || general.Manager == null)
                {
                    perBuildTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
                    general = perBuildTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
                }

                if (general == null || general.Manager == null)
                {
                    Debug.LogWarning("[AR Bloom] XR General Settings Android gagal dibuat otomatis.");
                    return;
                }

                general.InitManagerOnStart = true;
                bool assigned = XRPackageMetadataStore.AssignLoader(general.Manager, LoaderType, BuildTargetGroup.Android);
                EditorUtility.SetDirty(perBuildTarget);
                EditorUtility.SetDirty(general);
                EditorUtility.SetDirty(general.Manager);

                if (!assigned)
                    Debug.Log("[AR Bloom] ARCore Loader sudah aktif atau tidak perlu ditambahkan ulang.");
                else
                    Debug.Log("[AR Bloom] ARCore Loader diaktifkan untuk Android.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AR Bloom] Auto-enable ARCore gagal: " + ex.Message +
                                 "\nBuka Edit > Project Settings > XR Plug-in Management > Android dan centang ARCore jika diperlukan.");
            }
        }

        private static void EnsureScene()
        {
            Directory.CreateDirectory("Assets/Scenes");

            Scene scene;
            if (File.Exists(ScenePath))
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                if (UnityEngine.Object.FindFirstObjectByType<ARBloomApp>() == null)
                {
                    var root = new GameObject("AR Bloom App");
                    root.AddComponent<ARBloomApp>();
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene, ScenePath);
                }
            }
            else
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var root = new GameObject("AR Bloom App");
                root.AddComponent<ARBloomApp>();
                EditorSceneManager.SaveScene(scene, ScenePath);
            }

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
        }
    }
}
#endif
