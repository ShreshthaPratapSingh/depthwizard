// =============================================================================
// BuildDepthWizard.cs (Editor-only)
//
// Standalone Windows build script for DepthWizard.
//
// Usage from Unity menu: Tools > DepthWizard > Build Standalone (Windows)
//
// Or from CLI (headless):
//   Unity.exe -batchmode -nographics -projectPath <path> \
//     -executeMethod BuildDepthWizard.BuildWindows -quit
//
// Output: unity-client/Build/DepthWizard.exe
//
// Scenes are listed explicitly (LandingPage first, then SampleScene) to
// ensure correct build-time scene ordering regardless of EditorBuildSettings.
//
// Includes a build pre-processor that creates the URP Terrain/Lit material
// asset in Resources/ — this ensures Unity's shader variant stripping
// includes the correct shader variants needed for runtime terrain rendering.
// =============================================================================

using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildDepthWizard
{
    private const string BUILD_DIR = "Build";
    private const string EXE_NAME = "DepthWizard.exe";

    private const string TERRAIN_MAT_PATH = "Assets/Resources/TerrainLitFallback.mat";
    private const string TERRAIN_SHADER_NAME = "Universal Render Pipeline/Terrain/Lit";

    // Scenes MUST be in this order: LandingPage loads first, SampleScene
    // is loaded by ProcessingController after pipeline completion.
    private static readonly string[] SCENES = new[]
    {
        "Assets/Scenes/LandingPage.unity",
        "Assets/Scenes/SampleScene.unity",
    };

    /// <summary>
    /// Create/update the fallback terrain material in Resources/ so
    /// Unity's build pipeline compiles the correct shader variants.
    /// Can also be called manually from the menu.
    /// </summary>
    [MenuItem("Tools/DepthWizard/Create Terrain Material")]
    public static void EnsureTerrainMaterial()
    {
        // Make sure Resources/ folder exists
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            AssetDatabase.CreateFolder("Assets", "Resources");

        Shader shader = Shader.Find(TERRAIN_SHADER_NAME);
        if (shader == null)
        {
            Debug.LogError(
                $"[BuildDepthWizard] Shader '{TERRAIN_SHADER_NAME}' not found. " +
                "Is the Universal RP package installed?");
            return;
        }

        // Check if material already exists and has the correct shader
        var existing = AssetDatabase.LoadAssetAtPath<Material>(TERRAIN_MAT_PATH);
        if (existing != null && existing.shader == shader)
        {
            Debug.Log("[BuildDepthWizard] TerrainLitFallback material already up to date.");
            return;
        }

        // Create a new Material through the AssetDatabase API so Unity
        // properly tracks shader variant usage for build stripping.
        Material mat = new Material(shader);
        mat.name = "TerrainLitFallback";

        // Kill specular / smoothness
        mat.SetFloat("_Smoothness", 0f);
        mat.SetFloat("_Metallic", 0f);
        for (int i = 0; i < 8; i++)
        {
            mat.SetFloat($"_Smoothness{i}", 0f);
            mat.SetFloat($"_Metallic{i}", 0f);
        }

        mat.SetFloat("_SpecularHighlights", 0f);
        mat.SetFloat("_EnvironmentReflections", 0f);
        mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

        if (existing != null)
        {
            // Overwrite existing asset to preserve GUID references
            EditorUtility.CopySerialized(mat, existing);
            Object.DestroyImmediate(mat);
            Debug.Log("[BuildDepthWizard] TerrainLitFallback material updated.");
        }
        else
        {
            AssetDatabase.CreateAsset(mat, TERRAIN_MAT_PATH);
            Debug.Log("[BuildDepthWizard] TerrainLitFallback material created.");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Tools/DepthWizard/Build Standalone (Windows)")]
    public static void BuildWindows()
    {
        // Ensure the terrain material exists before building
        EnsureTerrainMaterial();

        string buildPath = System.IO.Path.Combine(BUILD_DIR, EXE_NAME);

        Debug.Log($"[BuildDepthWizard] Building to: {buildPath}");
        Debug.Log($"[BuildDepthWizard] Scenes: {string.Join(", ", SCENES)}");

        var options = new BuildPlayerOptions
        {
            scenes = SCENES,
            locationPathName = buildPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"[BuildDepthWizard] ✓ Build succeeded: {summary.totalSize / (1024 * 1024)} MB, " +
                      $"{summary.totalTime.TotalSeconds:F1}s");
        }
        else
        {
            Debug.LogError($"[BuildDepthWizard] ✗ Build failed: {summary.result}");
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type == LogType.Error || msg.type == LogType.Warning)
                        Debug.LogError($"  [{msg.type}] {msg.content}");
                }
            }
        }
    }
}

/// <summary>
/// Build pre-processor that ensures the URP Terrain/Lit material
/// exists in Resources/ before every build — even builds triggered
/// from File > Build or the Build Settings dialog (not just our menu).
/// </summary>
public class TerrainMaterialBuildPreprocessor : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        BuildDepthWizard.EnsureTerrainMaterial();
    }
}
