using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Nectorial.SlideEscape.Unity;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nectorial.Editor
{
    // Batch-only build of the separate "Few Moves 3D Preview" WebGL product.
    // The normal Few Moves build (WebGLBootstrap) and its settings are not modified; every PlayerSettings
    // value and the scene list touched here are restored in finally.
    public static class Preview3DBuild
    {
        private const string ScenePath = "Assets/Scenes/Preview3D.unity";
        private const string ModelPath = "Assets/Nectorial/Resources/Visuals3D/few-moves-pieces.fbx";
        private const string LitShaderPath = "Assets/Nectorial/Resources/Visuals3D/Preview3DLit.shader";
        private const string ShadowShaderPath = "Assets/Nectorial/Resources/Visuals3D/Preview3DContactShadow.shader";
        private const string AtlasPath = "Assets/Nectorial/Resources/Visuals/turn-escape-tiles.png";
        private const string RoomPath = "Assets/Nectorial/Resources/SlideRooms/room-01.json";
        private const string TemplatePath = "Assets/WebGLTemplates/TurnEscape/index.html";
        private const string StateBridgePath = "Assets/Plugins/WebGL/NectorialState.jslib";

        public static void Build()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Preview3DBuild.Build is batch-mode only.");

            string outputPath = ReadArgument("-buildOutput");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new InvalidOperationException("Missing required -buildOutput argument.");
            if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
                throw new InvalidOperationException("Preview output must be new or empty: " + outputPath);
            // An interrupted earlier run can leave preview values in ProjectSettings; restoring those in finally
            // would persist them. Refuse to start until the normal product settings are back.
            if (string.Equals(PlayerSettings.productName, Preview3DAssetCheck.ProductName, StringComparison.Ordinal) ||
                EditorBuildSettings.scenes.Any(scene => scene.path == ScenePath))
                throw new InvalidOperationException(
                    "ProjectSettings still hold a previous preview build's values; restore them before building.");

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
                ConfigureModelImport();
                ValidateImportedMeshes();
                EnsurePreviewScene();
                ConfigurePlayer();

                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = outputPath,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None
                });

                BuildSummary summary = report.summary;
                Debug.Log(
                    $"PREVIEW3D_BUILD_RESULT result={summary.result} " +
                    $"errors={summary.totalErrors} warnings={summary.totalWarnings} " +
                    $"bytes={summary.totalSize} output={outputPath}");

                if (summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException(
                        $"3D preview build failed: {summary.result}, errors={summary.totalErrors}");
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
                AssetDatabase.SaveAssets();
            }
        }

        // Batch entry that only imports and checks the frozen FBX against the mesh contract; no scene or build.
        public static void ValidateAssets()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Preview3DBuild.ValidateAssets is batch-mode only.");
            ValidateRequiredAssets();
            ConfigureModelImport();
            ValidateImportedMeshes();
        }

        // Import settings for the copied FBX only: meshes as-is, no embedded materials, animation, cameras or lights.
        private static void ConfigureModelImport()
        {
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("FBX is not imported as a model: " + ModelPath);

            bool changed = importer.materialImportMode != ModelImporterMaterialImportMode.None ||
                           importer.importAnimation || importer.importCameras || importer.importLights ||
                           importer.isReadable || importer.meshCompression != ModelImporterMeshCompression.Off ||
                           importer.addCollider;
            if (!changed) return;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.isReadable = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.addCollider = false;
            importer.SaveAndReimport();
        }

        // Same checks the runtime performs, run at build time so a contract mismatch never ships.
        private static void ValidateImportedMeshes()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null) throw new InvalidOperationException("Imported model is missing: " + ModelPath);
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "PREVIEW3D_MESH name={0} vertices={1} triangles={2} local_bounds_size=({3:0.###},{4:0.###},{5:0.###}) " +
                    "rotation=({6:0.#},{7:0.#},{8:0.#}) scale=({9:0.###},{10:0.###},{11:0.###})",
                    filter.gameObject.name, mesh.vertexCount, mesh.triangles.Length / 3,
                    mesh.bounds.size.x, mesh.bounds.size.y, mesh.bounds.size.z,
                    filter.transform.eulerAngles.x, filter.transform.eulerAngles.y, filter.transform.eulerAngles.z,
                    filter.transform.lossyScale.x, filter.transform.lossyScale.y, filter.transform.lossyScale.z));
            }

            if (!Preview3DAssetCheck.TryValidate(out var report, out string error))
                throw new InvalidOperationException("3D mesh contract check failed: " + error);
            foreach (string line in report) Debug.Log("PREVIEW3D_CONTRACT " + line);
        }

        private static void EnsurePreviewScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                // The runtime creates and configures its own camera and board; the scene only needs a camera.
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color32(0xf0, 0xec, 0xe2, 0xff);
                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                    throw new InvalidOperationException("Failed to save preview scene at " + ScenePath);
            }

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Independent";
            PlayerSettings.productName = Preview3DAssetCheck.ProductName;
            PlayerSettings.bundleVersion = "0.0.1-3d-preview";
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
            if (AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath) == null)
                throw new InvalidOperationException("Frozen FBX has not been copied in: " + ModelPath);
            if (AssetDatabase.LoadAssetAtPath<Shader>(LitShaderPath) == null ||
                AssetDatabase.LoadAssetAtPath<Shader>(ShadowShaderPath) == null)
                throw new InvalidOperationException("Preview shaders are missing.");
            Texture2D atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);
            if (atlas == null || atlas.width != 128 || atlas.height != 16)
                throw new InvalidOperationException("Required 128x16 tile atlas is missing: " + AtlasPath);
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(RoomPath) == null)
                throw new InvalidOperationException("Required room data is missing: " + RoomPath);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(TemplatePath)))
                throw new InvalidOperationException("Required WebGL template is missing: " + TemplatePath);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(StateBridgePath)) ||
                AssetImporter.GetAtPath(StateBridgePath) == null)
                throw new InvalidOperationException("Required WebGL state bridge is missing: " + StateBridgePath);
        }

        private static string ReadArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
    }
}
