using UnityEngine;
using DepthWizard.Networking;

public class TestTrigger : MonoBehaviour
{
    [SerializeField] private string testImagePath = @"C:\Users\user\Downloads\ManaliSattliteimagesnapshot.jpeg";

    void Start()
    {
        GetComponent<BackendClient>().UploadAndGenerate(testImagePath);
    }
}
