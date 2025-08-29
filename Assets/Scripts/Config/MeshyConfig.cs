using UnityEngine;

[CreateAssetMenu(menuName = "Meshy/Meshy Config", fileName = "MeshyConfig")]
public class MeshyConfig : ScriptableObject
{
    [Header("API")]
    [Tooltip("Do NOT commit your real API key to the repo. Leave empty in repo and inject at runtime / use a server proxy.")]
    public string apiKey = "";
    public string meshyApiUrl = "https://api.meshy.ai/openapi/v2/text-to-3d";
    public string meshyTaskStatusUrlBase = "https://api.meshy.ai/openapi/v2/text-to-3d/";

    [Header("Polling")]
    public float pollingIntervalSeconds = 5f;
    public float maxPollingTimeSeconds = 600f;

    [Header("Import / Limits")]
    public int maxVerticesForQuest = 60000; // conservative default; test and tune on device
    public string cacheFolder = "MeshyCache";
}