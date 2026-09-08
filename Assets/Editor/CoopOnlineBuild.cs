using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nectorial.Editor
{
    public static class CoopOnlineBuild
    {
        private const string ScenePath = "Assets/Scenes/Bootstrap.unity";
        private const string RoomPath = "Assets/Nectorial/Resources/CoopRooms/coop-c1.json";
        private const string TemplatePath = "Assets/WebGLTemplates/CoopOnline/index.html";
        private const string BridgePath = "Assets/Plugins/WebGL/CoopOnlineNetwork.jslib";

        public static void Build()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("CoopOnlineBuild.Build is batch-mode only.");
            string outputPath = ReadArgument("-buildOutput");
            if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("Missing required -buildOutput argument.");

            string previousProductName = PlayerSettings.productName;
            string previousBundleVersion = PlayerSettings.bundleVersion;
            string previousTemplate = PlayerSettings.WebGL.template;
            try
            {
                ValidateRequiredAssets();
                EnsureBootstrapScene();
                PlayerSettings.companyName = "Independent";
                PlayerSettings.productName = "Few Moves Online Pilot";
                PlayerSettings.bundleVersion = "0.0.1-online";
                PlayerSettings.runInBackground = true;
                PlayerSettings.stripEngineCode = false;
                PlayerSettings.SplashScreen.show = false;
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
                PlayerSettings.WebGL.decompressionFallback = false;
                PlayerSettings.WebGL.dataCaching = false;
                PlayerSettings.WebGL.template = "PROJECT:CoopOnline";
                CoopSerializationChecks.Run();

                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = outputPath,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None
                });
                BuildSummary summary = report.summary;
                Debug.Log($"COOP_ONLINE_WEBGL_RESULT result={summary.result} errors={summary.totalErrors} warnings={summary.totalWarnings} bytes={summary.totalSize} output={outputPath} template=PROJECT:CoopOnline entry=CoopOnlineBuild.Build");
                if (summary.result != BuildResult.Succeeded) throw new InvalidOperationException($"Online coop WebGL build failed: {summary.result}");
            }
            finally
            {
                PlayerSettings.productName = previousProductName;
                PlayerSettings.bundleVersion = previousBundleVersion;
                PlayerSettings.WebGL.template = previousTemplate;
            }
        }

        private static void ValidateRequiredAssets()
        {
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(RoomPath) == null) throw new InvalidOperationException("Missing coop room: " + RoomPath);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(TemplatePath))) throw new InvalidOperationException("Missing online template: " + TemplatePath);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(BridgePath)) || AssetImporter.GetAtPath(BridgePath) == null) throw new InvalidOperationException("Missing online bridge: " + BridgePath);
        }

        private static void EnsureBootstrapScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                return;
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            cameraObject.AddComponent<Camera>();
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Failed to save bootstrap scene.");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static string ReadArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
    }
}
