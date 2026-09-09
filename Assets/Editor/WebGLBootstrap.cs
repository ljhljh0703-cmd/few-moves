using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nectorial.Editor
{
    public static class WebGLBootstrap
    {
        private const string ScenePath = "Assets/Scenes/Bootstrap.unity";
        private const string AtlasPath = "Assets/Nectorial/Resources/Visuals/turn-escape-tiles.png";
        private const string RoomPath = "Assets/Nectorial/Resources/SlideRooms/room-01.json";
        private const string TemplatePath = "Assets/WebGLTemplates/TurnEscape/index.html";
        private const string StateBridgePath = "Assets/Plugins/WebGL/NectorialState.jslib";

        public static void Build()
        {
            if (!Application.isBatchMode)
            {
                throw new InvalidOperationException("WebGLBootstrap.Build is batch-mode only.");
            }

            var outputPath = ReadArgument("-buildOutput");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new InvalidOperationException("Missing required -buildOutput argument.");
            }

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

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = outputPath,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None
                });

                var summary = report.summary;
                Debug.Log(
                    $"WEBGL_BOOTSTRAP_RESULT result={summary.result} " +
                    $"errors={summary.totalErrors} warnings={summary.totalWarnings} " +
                    $"bytes={summary.totalSize} output={outputPath}");

                if (summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException(
                        $"WebGL bootstrap build failed: {summary.result}, errors={summary.totalErrors}");
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

        private static void EnsureBootstrapScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";

            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.047f, 0.063f, 1f);
            cameraObject.transform.position = new Vector3(0f, 0f, -10f);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                throw new InvalidOperationException($"Failed to save bootstrap scene at {ScenePath}.");
            }

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Independent";
            PlayerSettings.productName = "Few Moves";
            PlayerSettings.bundleVersion = "0.0.1";
            PlayerSettings.runInBackground = true;
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = false;
            PlayerSettings.WebGL.template = "PROJECT:TurnEscape";
        }

        private static void ValidateRequiredAssets()
        {
            Texture2D atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);
            if (atlas == null)
            {
                throw new InvalidOperationException("Required tile atlas is missing: " + AtlasPath);
            }

            if (atlas.width != 128 || atlas.height != 16)
            {
                throw new InvalidOperationException(
                    $"Tile atlas must be 128x16, got {atlas.width}x{atlas.height}: {AtlasPath}");
            }

            if (AssetDatabase.LoadAssetAtPath<TextAsset>(RoomPath) == null)
            {
                throw new InvalidOperationException("Required room data is missing: " + RoomPath);
            }

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(TemplatePath)))
            {
                throw new InvalidOperationException("Required WebGL template is missing: " + TemplatePath);
            }

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(StateBridgePath)) ||
                AssetImporter.GetAtPath(StateBridgePath) == null)
            {
                throw new InvalidOperationException("Required WebGL state bridge is missing: " + StateBridgePath);
            }
        }

        private static string ReadArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
    }
}
