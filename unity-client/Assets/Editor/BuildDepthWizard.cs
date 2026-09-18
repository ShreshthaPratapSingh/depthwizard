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
// =============================================================================

using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildDepthWizard
{
    private const string BUILD_DIR = "Build";
    private const string EXE_NAME = "DepthWizard.exe";

    // Scenes MUST be in this order: LandingPage loads first, SampleScene
    // is loaded by ProcessingController after pipeline completion.
    private static readonly string[] SCENES = new[]
    {
        "Assets/Scenes/LandingPage.unity",
        "Assets/Scenes/SampleScene.unity",
    };

    [MenuItem("Tools/DepthWizard/Build Standalone (Windows)")]
    public static void BuildWindows()
    {
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
