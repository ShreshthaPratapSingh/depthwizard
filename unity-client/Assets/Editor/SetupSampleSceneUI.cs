// =============================================================================
// SetupSampleSceneUI.cs (Editor-only)
//
// Adds the ExportButtonHandler component to the main camera GameObject in
// SampleScene, so the export HUD overlay appears when the scene loads.
//
// Menu: Tools > DepthWizard > Setup SampleScene UI
// =============================================================================

using UnityEditor;
using UnityEngine;

public static class SetupSampleSceneUI
{
    [MenuItem("Tools/DepthWizard/Setup SampleScene Export UI")]
    public static void Setup()
    {
        // Find the main camera (usually has BackendClient + TestTrigger)
        var cam = UnityEngine.Camera.main;
        if (cam == null)
        {
            EditorUtility.DisplayDialog(
                "Setup SampleScene UI",
                "No Main Camera found. Open SampleScene first.",
                "OK");
            return;
        }

        // Check if already set up
        var existing = cam.GetComponent<DepthWizard.UI.ExportButtonHandler>();
        if (existing != null)
        {
            EditorUtility.DisplayDialog(
                "Setup SampleScene UI",
                "ExportButtonHandler is already attached to the Main Camera.",
                "OK");
            return;
        }

        // Add the component
        Undo.AddComponent<DepthWizard.UI.ExportButtonHandler>(cam.gameObject);

        EditorUtility.SetDirty(cam.gameObject);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            cam.gameObject.scene);

        Debug.Log("[SetupSampleSceneUI] Added ExportButtonHandler to Main Camera. Save the scene.");
        EditorUtility.DisplayDialog(
            "Setup SampleScene UI",
            "ExportButtonHandler added to Main Camera.\n\n" +
            "Remember to save the scene (Ctrl+S).",
            "OK");
    }
}
