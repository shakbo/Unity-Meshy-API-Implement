using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityFigmaBridge.Editor.Utils;
#if GLTFAST_PRESENT
using GLTFast;
#endif

/// <summary>
/// MeshyImporter:
/// - Downloads a remote glTF/glb URL and imports it into the scene using glTFast.
/// - Performs basic vertex count check against MeshyConfig.maxVerticesForQuest and warns if exceeding.
/// - Returns instantiated GameObject root.
/// 
/// Requires glTFast package (com.unity.cloud.gltfast).
/// </summary>
public class MeshyImporter : MonoBehaviour
{
    public MeshyConfig config;

    public void Initialize(MeshyConfig cfg)
    {
        config = cfg;
    }

    public async Task<GameObject> ImportFromUrlAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) throw new ArgumentException(nameof(url));
        if (config == null) Debug.LogWarning("MeshyImporter: config is null - proceeding with defaults.");

        string cacheDir = Path.Combine(Application.persistentDataPath, config != null ? config.cacheFolder : "MeshyCache");
        if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

        string fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(fileName)) fileName = "meshy_model.glb";
        string localPath = Path.Combine(cacheDir, fileName);

        // Simple download
        using (var uw = UnityEngine.Networking.UnityWebRequest.Get(url))
        {
            uw.downloadHandler = new UnityEngine.Networking.DownloadHandlerFile(localPath);
#if UNITY_2020_1_OR_NEWER
            await uw.SendWebRequest();
            if (uw.result == UnityEngine.Networking.UnityWebRequest.Result.ConnectionError || uw.result == UnityEngine.Networking.UnityWebRequest.Result.ProtocolError)
#else
            await uw.SendWebRequest();
            if (uw.isNetworkError || uw.isHttpError)
#endif
            {
                Debug.LogError($"MeshyImporter: download failed: {uw.error}");
                return null;
            }
        }

#if GLTFAST_PRESENT
        // Load with glTFast
        try
        {
            var instantiator = new GameObject("MeshyModel");
            var gltf = new GltfImport();
            bool success = await gltf.Load(localPath);
            if (!success)
            {
                Debug.LogError("MeshyImporter: glTF load failed.");
                return null;
            }

            bool ok = await gltf.InstantiateMainSceneAsync(instantiator.transform);
            if (!ok)
            {
                Debug.LogError("MeshyImporter: instantiate failed.");
                return null;
            }

            // optional vertex count check
            int totalVerts = 0;
            var filters = instantiator.GetComponentsInChildren<MeshFilter>();
            foreach (var f in filters) if (f.sharedMesh != null) totalVerts += f.sharedMesh.vertexCount;
            if (config != null && config.maxVerticesForQuest > 0 && totalVerts > config.maxVerticesForQuest)
            {
                Debug.LogWarning($"Imported model has {totalVerts} verts which exceeds max {config.maxVerticesForQuest} for Quest - consider decimating.");
            }

            return instantiator;
        }
        catch (Exception ex)
        {
            Debug.LogError($"MeshyImporter: exception while importing: {ex}");
            return null;
        }
#else
        Debug.LogWarning("GLTFast not present - imported file saved to disk at: " + localPath + ". Please install glTFast to auto-import.");
        // Return a placeholder empty root pointing to local file
        var root = new GameObject("MeshyModel_Root");
        root.AddComponent<MeshyImportPlaceholder>().localPath = localPath;
        return root;
#endif
    }
}

public class MeshyImportPlaceholder : MonoBehaviour
{
    public string localPath;
}