// =============================================================================
// SetupSampleSceneUI.cs (Editor-only)
//
// Adds the ExportButtonHandler and ControlHintsOverlay components to the main
// camera GameObject in SampleScene, so the HUD overlays appear at runtime.
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

        bool changed = false;

        // --- ExportButtonHandler ---
        if (cam.GetComponent<DepthWizard.UI.ExportButtonHandler>() == null)
        {
            Undo.AddComponent<DepthWizard.UI.ExportButtonHandler>(cam.gameObject);
            changed = true;
        }

        // --- ControlHintsOverlay ---
        if (cam.GetComponent<DepthWizard.UI.ControlHintsOverlay>() == null)
        {
            Undo.AddComponent<DepthWizard.UI.ControlHintsOverlay>(cam.gameObject);
            changed = true;
        }

        if (!changed)
        {
            EditorUtility.DisplayDialog(
                "Setup SampleScene UI",
                "All HUD components are already attached.",
                "OK");
            return;
        }

        EditorUtility.SetDirty(cam.gameObject);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            cam.gameObject.scene);

        Debug.Log("[SetupSampleSceneUI] HUD components added to Main Camera. Save the scene.");
        EditorUtility.DisplayDialog(
            "Setup SampleScene UI",
            "ExportButtonHandler + ControlHintsOverlay added to Main Camera.\n\n" +
            "Remember to save the scene (Ctrl+S).",
            "OK");
    }
}
