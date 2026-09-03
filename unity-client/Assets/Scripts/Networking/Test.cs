using UnityEngine;
using DepthWizard.Networking;
using DepthWizard.UI;

public class TestTrigger : MonoBehaviour
{
    [Header("Sample mode (preferred for quick demo)")]
    [Tooltip("If set, calls POST /process-sample/{sampleId} on Start.")]
    [SerializeField] private string sampleId = "delhi_urban";

    [Header("File upload mode (set sampleId empty to use this)")]
    [Tooltip("Absolute path to a local image file.")]
    [SerializeField] private string testImagePath = "";

    void Start()
    {
        var client = GetComponent<BackendClient>();

        if (FindFirstObjectByType<Terrain>() != null)
        {
            Debug.Log("[TestTrigger] Terrain already exists; skipping a second process.");
            return;
        }

        var session = ImageSessionManager.Instance;
        if (session != null && !string.IsNullOrEmpty(session.SampleId))
        {
            Debug.Log($"[TestTrigger] Using sample from landing page: {session.SampleId}");
            client.ProcessSample(session.SampleId);
            return;
        }

        // --- Priority 1: Image selected from the Landing Page ---
        if (session != null && session.HasImage)
        {
            Debug.Log($"[TestTrigger] Using image from landing page: {session.FilePath}");
            client.UploadAndGenerate(session.FilePath);
            return;
        }

        // --- Priority 2: Hardcoded sample ID (for dev/demo without landing page) ---
        if (!string.IsNullOrEmpty(sampleId))
        {
            client.ProcessSample(sampleId);
        }
        // --- Priority 3: Hardcoded file path (for dev testing) ---
        else if (!string.IsNullOrEmpty(testImagePath))
        {
            client.UploadAndGenerate(testImagePath);
        }
        else
        {
            Debug.LogWarning("[TestTrigger] No image source available. " +
                "Select an image from the Landing Page, or set sampleId/testImagePath in the Inspector.");
        }
    }
}
