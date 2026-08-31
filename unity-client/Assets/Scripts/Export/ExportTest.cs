// Attach to any GameObject, hit Play, generate terrain, press T to export.
using UnityEngine;
using DepthWizard.Export;

public class ExportTest : MonoBehaviour
{
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.T))
        {
            string objPath = System.IO.Path.Combine(Application.dataPath, "../Exports/terrain_export.obj");
            string hmPath  = System.IO.Path.Combine(Application.dataPath, "../Exports/terrain_heightmap.png");

            TerrainExporter.ExportCurrentTerrainAsObj(objPath);
            TerrainExporter.ExportCurrentTerrainAsHeightmapPng(hmPath);

            Debug.Log($"Export directory: {System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../Exports"))}");
        }
    }
}
