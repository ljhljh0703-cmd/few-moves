using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Nectorial.SlideEscape.Raid;

namespace Nectorial.Editor
{
    public static class RaidBuild
    {
        private const string ScenePath = "Assets/Scenes/Bootstrap.unity";
        private const string ArenaPath = RaidContent.DefaultArenaAssetPath;
        private const string TemplatePath = "Assets/WebGLTemplates/Raid/index.html";
        private const string StateBridgePath = "Assets/Plugins/WebGL/RaidState.jslib";
        private const string ProductName = "Few Moves Raid Pilot";

        public static void Build()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("RaidBuild.Build is batch-mode only.");
            string outputPath = ReadArgument("-buildOutput");
            if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("Missing required -buildOutput argument.");

            string previousCompanyName = PlayerSettings.companyName;
            string previousProductName = PlayerSettings.productName;
            string previousBundleVersion = PlayerSettings.bundleVersion;
            bool previousRunInBackground = PlayerSettings.runInBackground;
            bool previousStripEngineCode = PlayerSettings.stripEngineCode;
            bool previousSplash = PlayerSettings.SplashScreen.show;
            string previousTemplate = PlayerSettings.WebGL.template;
            WebGLCompressionFormat previousCompression = PlayerSettings.WebGL.compressionFormat;
            bool previousDecompressionFallback = PlayerSettings.WebGL.decompressionFallback;
            bool previousDataCaching = PlayerSettings.WebGL.dataCaching;
            EditorBuildSettingsScene[] previousScenes = EditorBuildSettings.scenes;

            try
            {
                ValidateRequiredAssets();
                EnsureBootstrapScene();
                ConfigurePlayer();
                RunProbeHook();

                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = outputPath,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None
                });
                BuildSummary summary = report.summary;
                Debug.Log($"RAID_WEBGL_RESULT result={summary.result} errors={summary.totalErrors} warnings={summary.totalWarnings} bytes={summary.totalSize} output={outputPath} template=PROJECT:Raid entry=RaidBuild.Build");
                if (summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException($"Raid WebGL build failed: {summary.result}, errors={summary.totalErrors}");
            }
            finally
            {
                PlayerSettings.companyName = previousCompanyName;
                PlayerSettings.productName = previousProductName;
                PlayerSettings.bundleVersion = previousBundleVersion;
                PlayerSettings.runInBackground = previousRunInBackground;
                PlayerSettings.stripEngineCode = previousStripEngineCode;
                PlayerSettings.SplashScreen.show = previousSplash;
                PlayerSettings.WebGL.template = previousTemplate;
                PlayerSettings.WebGL.compressionFormat = previousCompression;
                PlayerSettings.WebGL.decompressionFallback = previousDecompressionFallback;
                PlayerSettings.WebGL.dataCaching = previousDataCaching;
                EditorBuildSettings.scenes = previousScenes;
            }
        }

        private static void RunProbeHook()
        {
            TextAsset arenaAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ArenaPath);
            if (arenaAsset == null || string.IsNullOrWhiteSpace(arenaAsset.text))
                throw new InvalidOperationException("Raid probe could not load arena JSON: " + ArenaPath);

            Type arenaType = FindType("Nectorial.SlideEscape.Raid.RaidArenaDefinition");
            if (arenaType == null) throw new InvalidOperationException("Raid probe could not find RaidArenaDefinition.");
            object arena;
            try { arena = JsonUtility.FromJson(arenaAsset.text, arenaType); }
            catch (Exception exception) { throw new InvalidOperationException("Raid arena JSON probe failed.", exception); }
            if (arena == null) throw new InvalidOperationException("Raid arena JSON probe returned null.");

            Type rulesType = FindType("Nectorial.SlideEscape.Raid.RaidRules");
            if (rulesType == null) throw new InvalidOperationException("Raid probe could not find RaidRules.");
            MethodInfo validate = rulesType.GetMethod("ValidateArena", BindingFlags.Public | BindingFlags.Static);
            if (validate == null) throw new InvalidOperationException("Raid probe could not find RaidRules.ValidateArena.");
            object result = validate.Invoke(null, new[] { arena });
            string[] errors = result as string[];
            if (errors == null) throw new InvalidOperationException("Raid arena validation returned an unexpected result.");
            if (errors.Length > 0)
                throw new InvalidOperationException("Raid arena validation failed: " + errors[0]);
            RaidSerializationChecks.Run();
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static void EnsureBootstrapScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                return;
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.047f, 0.063f, 1f);
            cameraObject.transform.position = new Vector3(0f, 0f, -10f);
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Failed to save bootstrap scene at " + ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Independent";
            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = "0.1.0-raid";
            PlayerSettings.runInBackground = true;
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = false;
            PlayerSettings.WebGL.template = "PROJECT:Raid";
        }

        private static void ValidateRequiredAssets()
        {
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(ArenaPath) == null) throw new InvalidOperationException("Required raid arena is missing: " + ArenaPath);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(TemplatePath))) throw new InvalidOperationException("Required Raid WebGL template is missing: " + TemplatePath);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(StateBridgePath)) || AssetImporter.GetAtPath(StateBridgePath) == null)
                throw new InvalidOperationException("Required Raid WebGL state bridge is missing: " + StateBridgePath);
        }

        private static string ReadArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
    }
}
