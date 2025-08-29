using System.Collections;
using UnityEngine;

/// <summary>
/// MeshyController: high-level coordinator that ties together API client, importer, and placement.
/// Usage:
/// - Assign a MeshyConfig asset, provide preview/final prefab for placement.
/// - Call StartGeneration with a prompt.
/// </summary>
public class MeshyController : MonoBehaviour
{
    public MeshyConfig config;
    public MeshyAPIClient apiClient;
    public MeshyImporter importer;
    public PlacementTool placementTool;

    void Awake()
    {
        if (apiClient != null) apiClient.Initialize(config);
        if (importer != null) importer.Initialize(config);
    }

    public void StartGeneration(string prompt)
    {
        if (apiClient == null || importer == null)
        {
            Debug.LogError("MeshyController: missing components");
            return;
        }

        StartCoroutine(GenerateCoroutine(prompt));
    }

    private IEnumerator GenerateCoroutine(string prompt)
    {
        string modelUrl = null;
        string error = null;
        yield return StartCoroutine(apiClient.SubmitPrompt(prompt, (url, err) => { modelUrl = url; error = err; }));

        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError("MeshyController: api error: " + error);
            yield break;
        }

        if (string.IsNullOrEmpty(modelUrl))
        {
            Debug.LogError("MeshyController: no model url returned");
            yield break;
        }

        var importTask = importer.ImportFromUrlAsync(modelUrl);
        while (!importTask.IsCompleted) yield return null;
        var root = importTask.Result;
        if (root == null)
        {
            Debug.LogError("MeshyController: import failed");
            yield break;
        }

        // Optionally provide preview object to placement tool
        if (placementTool != null && placementTool.previewPrefab == null)
        {
            placementTool.previewPrefab = root;
        }

        Debug.Log("MeshyController: model imported and ready for placement");
    }
}