using UnityEngine;
using DepthWizard.Networking;

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

        if (!string.IsNullOrEmpty(sampleId))
        {
            client.ProcessSample(sampleId);
        }
        else if (!string.IsNullOrEmpty(testImagePath))
        {
            client.UploadAndGenerate(testImagePath);
        }
        else
        {
            Debug.LogWarning("[TestTrigger] No sampleId or testImagePath set.");
        }
    }
}
