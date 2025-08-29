using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Minimal Meshy API client:
/// - Submits a text-to-3d task (simple JSON body)
/// - Polls status endpoint until complete or timeout
/// - Returns final model download URL (expected "model_url" in the JSON result)
/// NOTE: The API contract (field names) must match Meshy AI responses; adjust parsing if needed.
/// </summary>
public class MeshyAPIClient : MonoBehaviour
{
    public MeshyConfig config;

    public void Initialize(MeshyConfig cfg)
    {
        config = cfg;
    }

    public IEnumerator SubmitPrompt(string prompt, Action<string /*modelUrl or null*/, string /*error*/> onComplete)
    {
        if (config == null)
        {
            onComplete?.Invoke(null, "MeshyConfig not assigned.");
            yield break;
        }

        if (string.IsNullOrEmpty(config.apiKey))
        {
            onComplete?.Invoke(null, "API key missing. Configure MeshyConfig.apiKey at runtime or use a proxy.");
            yield break;
        }

        var body = new
        {
            prompt = prompt
            // extend this object per Meshy API docs (size, style etc.)
        };
        string json = JsonUtility.ToJson(body);

        using (UnityWebRequest uw = new UnityWebRequest(config.meshyApiUrl, "POST"))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            uw.uploadHandler = new UploadHandlerRaw(bytes);
            uw.downloadHandler = new DownloadHandlerBuffer();
            uw.SetRequestHeader("Content-Type", "application/json");
            uw.SetRequestHeader("Authorization", $"Bearer {config.apiKey}");

            yield return uw.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            if (uw.result == UnityWebRequest.Result.ConnectionError || uw.result == UnityWebRequest.Result.ProtocolError)
#else
            if (uw.isNetworkError || uw.isHttpError)
#endif
            {
                onComplete?.Invoke(null, $"Submit failed: {uw.error}");
                yield break;
            }
            // Expecting JSON with task id
            string resp = uw.downloadHandler.text;
            var taskId = ParseTaskId(resp);
            if (string.IsNullOrEmpty(taskId))
            {
                onComplete?.Invoke(null, "No task id returned from Meshy.");
                yield break;
            }

            // Poll status
            float start = Time.time;
            while (Time.time - start < config.maxPollingTimeSeconds)
            {
                string statusUrl = config.meshyTaskStatusUrlBase + taskId;
                using (UnityWebRequest uw2 = UnityWebRequest.Get(statusUrl))
                {
                    uw2.SetRequestHeader("Authorization", $"Bearer {config.apiKey}");
                    yield return uw2.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                    if (uw2.result == UnityWebRequest.Result.ConnectionError || uw2.result == UnityWebRequest.Result.ProtocolError)
#else
                    if (uw2.isNetworkError || uw2.isHttpError)
#endif
                    {
                        onComplete?.Invoke(null, $"Status poll failed: {uw2.error}");
                        yield break;
                    }

                    string statusResp = uw2.downloadHandler.text;
                    // Expecting JSON with { status: "...", model_url: "..." }
                    var status = ParseStatus(statusResp);
                    if (status.status == "succeeded" && !string.IsNullOrEmpty(status.modelUrl))
                    {
                        onComplete?.Invoke(status.modelUrl, null);
                        yield break;
                    }
                    else if (status.status == "failed")
                    {
                        onComplete?.Invoke(null, "Meshy task failed.");
                        yield break;
                    }
                }

                yield return new WaitForSeconds(config.pollingIntervalSeconds);
            }

            onComplete?.Invoke(null, "Timeout waiting for Meshy task.");
        }
    }

    // NOTE: These parsers are naive ¡X adapt to Meshy response shape.
    private string ParseTaskId(string json)
    {
        try
        {
            var wrapper = JsonUtility.FromJson<TaskIdWrapper>(json);
            return wrapper?.id;
        }
        catch { return null; }
    }

    private (string status, string modelUrl) ParseStatus(string json)
    {
        try
        {
            var s = JsonUtility.FromJson<StatusWrapper>(json);
            return (s.status, s.model_url);
        }
        catch { return (null, null); }
    }

    [Serializable]
    private class TaskIdWrapper
    {
        public string id;
    }

    [Serializable]
    private class StatusWrapper
    {
        public string status;
        public string model_url;
    }
}