using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Collections;
using System.Text;
using TMPro;
using System.Collections.Generic;
using System;
using UnityEngine.EventSystems; // Required for Event Trigger system

// Requires glTFast package: Window -> Package Manager -> + -> Add package from git URL -> com.unity.modules.gltfast
using GLTFast;
using GLTFast.Logging; // Optional, for ConsoleLogger
using System.Threading.Tasks; // Required for Task-based operations

public class MeshyController : MonoBehaviour
{
    [Header("API Configuration")]
    [SerializeField] private string apiKey = "YOUR_MESHY_API_KEY"; // --- PASTE YOUR KEY HERE ---
    [SerializeField] private string meshyApiUrl = "https://api.meshy.ai/openapi/v2/text-to-3d"; // Correct for POST
    [SerializeField] private string meshyTaskStatusUrlBase = "https://api.meshy.ai/openapi/v2/text-to-3d/"; // Correct base for GET
    [SerializeField] private float pollingIntervalSeconds = 5.0f;
    [SerializeField] private float maxPollingTimeSeconds = 600f; // 10 minutes timeout

    [Header("UI Elements")]
    [SerializeField] private TMP_InputField promptInput;
    [SerializeField] private Button generateButton; // Generates Preview
    [SerializeField] private Button refineButton;   // Refines Preview
    [SerializeField] private Button placeButton;    // Places Current Preview Model
    [SerializeField] private RawImage previewImage; // Shows RenderTexture output
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Preview Setup")]
    [SerializeField] private Camera previewCamera;
    [SerializeField] private GameObject previewModelContainer;
    [SerializeField] private LayerMask previewModelLayer; // Layer used ONLY for the preview model
    [SerializeField] private float previewPadding = 1.2f;
    [SerializeField] private float previewRotationSpeed = 0.4f; // Sensitivity for rotation

    [Header("Placement Setup")]
    [SerializeField] private LayerMask placementLayerMask = 1; // Layer(s) for placing model on
    [SerializeField] private Material transparentMaterial; // Material for placement ghost
    [SerializeField] private GameObject uiPanelToToggle; // Assign the parent UI Panel/CanvasGroup here
    [SerializeField] private KeyCode uiToggleKey = KeyCode.Tab; // Key to toggle UI
    private SimpleCameraController mainCameraController; // Reference to the script on the main camera

    // Internal State
    private GameObject currentPreviewModelInstance; // The model in the preview container
    private GameObject currentPlacementModelInstance; // The ghost model being placed
    private RenderTexture previewRenderTexture;
    private bool isPlacingModel = false;
    private List<Material> originalMaterials = new List<Material>();
    private string lastSuccessfulPreviewTaskId = null; // ID of the preview task to refine/place
    private bool isDraggingPreview = false; // Flag for rotating preview
    private bool isPlacementUIHidden = false; // Track UI state during placement

    // --- JSON Helper Classes (Verified against Docs) ---
    [System.Serializable] private class TextTo3DRequestPreview { public string mode = "preview"; public string prompt; public string art_style = "realistic"; public bool should_remesh = true; public int target_polycount = 30000; }
    [System.Serializable] private class TextTo3DRequestRefine { public string mode = "refine"; public string preview_task_id; public bool enable_pbr = true; }
    [System.Serializable] private class TaskCreateResponse { public string result; } // POST response
    [System.Serializable] private class TaskStatusResponse { public string id; public ModelUrls model_urls; public string thumbnail_url; public string prompt; public string art_style; public int progress; public long started_at; public long created_at; public long finished_at; public string status; public List<TextureInfo> texture_urls; public int preceding_tasks; public TaskError task_error; } // GET response
    [System.Serializable] private class ModelUrls { public string glb; public string fbx; public string obj; public string mtl; public string usdz; }
    [System.Serializable] private class TextureInfo { public string base_color; }
    [System.Serializable] private class TaskError { public string code; public string message; }
    // --- End JSON Helper Classes ---

    void Start()
    {
        mainCameraController = Camera.main.GetComponent<SimpleCameraController>();
        if (mainCameraController == null)
        {
            Debug.LogWarning("Main Camera does not have a SimpleCameraController component. Placement movement disabled.");
        }

        if (!ValidateConfiguration()) // Check if essential components are assigned
        {
            generateButton.interactable = false; // Disable if setup incomplete
            refineButton.interactable = false;
            placeButton.interactable = false;
            return;
        }

        SetupPreviewRendering(); // Link Preview Camera to RawImage via RenderTexture

        // Assign button listeners
        generateButton.onClick.AddListener(OnGeneratePreviewClick);
        refineButton.onClick.AddListener(OnRefineClick);
        placeButton.onClick.AddListener(OnPlaceButtonClick);

        // Set initial button states
        generateButton.interactable = true;
        refineButton.interactable = false;
        placeButton.interactable = false;

        // Setup listeners for preview rotation via code
        SetupEventTriggerListeners();

        SetStatus("Enter a prompt and click Generate Preview.");
    }

    // Basic validation for required Inspector assignments
    bool ValidateConfiguration()
    {
        bool isValid = true;
        if (string.IsNullOrEmpty(apiKey) || apiKey == "YOUR_MESHY_API_KEY") { SetStatus("Error: API Key not set!", true); isValid = false; }
        if (promptInput == null) { Debug.LogError("Prompt Input not assigned!"); isValid = false; }
        if (generateButton == null) { Debug.LogError("Generate Button not assigned!"); isValid = false; }
        if (refineButton == null) { Debug.LogError("Refine Button not assigned!"); isValid = false; }
        if (placeButton == null) { Debug.LogError("Place Button not assigned!"); isValid = false; }
        if (previewImage == null) { Debug.LogError("Preview Image (RawImage) not assigned!"); isValid = false; }
        if (statusText == null) { Debug.LogError("Status Text not assigned!"); isValid = false; }
        if (previewCamera == null) { Debug.LogError("Preview Camera not assigned!"); isValid = false; }
        if (previewModelContainer == null) { Debug.LogError("Preview Model Container not assigned!"); isValid = false; }
        if (transparentMaterial == null) { Debug.LogError("Transparent Material not assigned!"); isValid = false; }
        if (uiPanelToToggle == null) { Debug.LogError("UI Panel To Toggle not assigned!"); isValid = false; }
        if (previewModelLayer == 0) { Debug.LogError("Preview Model Layer is not set in Inspector!"); isValid = false; }
        else if (LayerMask.LayerToName(previewModelLayer).Length == 0 && previewModelLayer != 0)
        {
            // This checks if the layer assigned in the inspector actually exists
            // LayerMask stores a bitmask, LayerToName needs the index. We find the index from the bitmask.
            int layerIndex = 0;
            int layerValue = previewModelLayer.value;
            while ((layerValue >>= 1) > 0) layerIndex++; // Find the index of the first set bit
            if (LayerMask.LayerToName(layerIndex).Length == 0)
            {
                Debug.LogError($"Preview Model Layer (index {layerIndex}) selected in Inspector does not exist! Configure it in Project Settings > Tags and Layers.");
                isValid = false;
            }
        }
        return isValid;
    }

    // Sets up the RenderTexture link between the Preview Camera and the UI RawImage
    void SetupPreviewRendering()
    {
        if (previewRenderTexture != null) { /* Cleanup existing if needed */ }

        RectTransform rawImageRect = previewImage.GetComponent<RectTransform>();
        int width = Mathf.Max(1, (int)rawImageRect.rect.width);
        int height = Mathf.Max(1, (int)rawImageRect.rect.height);

        previewRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR); // Use HDR for better lighting if needed
        if (!previewRenderTexture.Create()) { Debug.LogError("Failed to create RenderTexture!"); return; }

        previewCamera.targetTexture = previewRenderTexture;
        previewImage.texture = previewRenderTexture;
        previewImage.color = Color.white; // Make RawImage visible

        // Configure Preview Camera and Container Layer
        int layerIndex = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
        if (layerIndex != -1)
        {
            SetLayerRecursively(previewModelContainer, layerIndex);
            previewCamera.cullingMask = 1 << layerIndex; // Set culling mask to ONLY the preview layer
        }
        else
        {
            Debug.LogError("Could not determine layer index from PreviewModelLayer mask.");
            previewCamera.cullingMask = 0; // Render nothing if layer is invalid
        }
    }

    // Adds listeners to the Event Trigger on the RawImage for drag rotation
    void SetupEventTriggerListeners()
    {
        EventTrigger trigger = previewImage.gameObject.GetComponent<EventTrigger>();
        if (trigger == null) { trigger = previewImage.gameObject.AddComponent<EventTrigger>(); } // Add if missing

        trigger.triggers.Clear(); // Clear existing to avoid duplicates

        AddEventTriggerListener(trigger, EventTriggerType.PointerDown, (data) => { OnPreviewPointerDown((PointerEventData)data); });
        AddEventTriggerListener(trigger, EventTriggerType.Drag, (data) => { OnPreviewDrag((PointerEventData)data); });
        AddEventTriggerListener(trigger, EventTriggerType.PointerUp, (data) => { OnPreviewPointerUp((PointerEventData)data); });
    }

    // Helper to add Event Trigger listeners cleanly
    void AddEventTriggerListener(EventTrigger trigger, EventTriggerType eventType, UnityEngine.Events.UnityAction<BaseEventData> action)
    {
        EventTrigger.Entry entry = new EventTrigger.Entry { eventID = eventType };
        entry.callback.AddListener(action);
        trigger.triggers.Add(entry);
    }

    void Update()
    {
        // Handle placement ghost movement if placing model
        if (isPlacingModel) // Changed condition to just check isPlacingModel
        {
            // --- ADD UI TOGGLE ---
            if (Input.GetKeyDown(uiToggleKey) && uiPanelToToggle != null)
            {
                isPlacementUIHidden = !isPlacementUIHidden;
                uiPanelToToggle.SetActive(!isPlacementUIHidden);
                // Update status to reflect toggle state if needed
                SetStatus($"Move mouse/camera to position. [{uiToggleKey}] Toggle UI. Left-Click: Place. Right-Click: Cancel.");
            }
            // --- END UI TOGGLE ---

            if (currentPlacementModelInstance != null)
            {
                HandlePlacement(); // Only handle placement if the instance exists
            }
        }
    }

    void OnDestroy()
    {
        // Release RenderTexture and destroy models when script is destroyed
        if (previewRenderTexture != null)
        {
            if (previewCamera != null) previewCamera.targetTexture = null;
            if (previewImage != null) previewImage.texture = null;
            previewRenderTexture.Release(); Destroy(previewRenderTexture);
        }
        if (currentPreviewModelInstance != null) Destroy(currentPreviewModelInstance);
        if (currentPlacementModelInstance != null) Destroy(currentPlacementModelInstance);
    }

    // Updates the status text UI
    void SetStatus(string message, bool isError = false)
    {
        if (statusText != null) { statusText.text = message; statusText.color = isError ? Color.red : Color.white; }
        if (isError) Debug.LogError(message); else Debug.Log(message);
    }

    // --- Button Click Handlers ---

    void OnGeneratePreviewClick()
    {
        string prompt = promptInput.text;
        if (string.IsNullOrWhiteSpace(prompt)) { SetStatus("Please enter a prompt.", true); return; }
        SetInteractableStates(false, false, false); // Disable buttons
        CleanupPreviousModelsAndPreview();
        StartCoroutine(StartPreviewGeneration(prompt));
    }

    void OnRefineClick()
    {
        if (string.IsNullOrEmpty(lastSuccessfulPreviewTaskId)) { SetStatus("No successful preview available to refine.", true); return; }
        SetInteractableStates(false, false, false); // Disable buttons
        StartCoroutine(StartRefineGeneration(lastSuccessfulPreviewTaskId));
    }

    void OnPlaceButtonClick()
    {
        if (currentPreviewModelInstance == null) { SetStatus("No model available in preview to place.", true); return; }
        if (currentPlacementModelInstance != null) Destroy(currentPlacementModelInstance);

        currentPlacementModelInstance = Instantiate(currentPreviewModelInstance);
        currentPlacementModelInstance.name = "Placement_" + currentPreviewModelInstance.name;
        currentPlacementModelInstance.SetActive(true);
        // currentPlacementModelInstance.transform.rotation = Quaternion.identity; // Optional: Reset rotation

        SetLayerRecursively(currentPlacementModelInstance, LayerMask.NameToLayer("Default")); // Move to default layer
        SetModelTransparency(currentPlacementModelInstance, true); // Make transparent

        isPlacingModel = true;
        SetInteractableStates(false, false, false); // Disable buttons during placement
        SetStatus("Move mouse to position, Left-Click to place, Right-Click to cancel.");

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(false); // Hide UI initially
        isPlacementUIHidden = true; // Track state
        if (mainCameraController != null) mainCameraController.SetActive(true); // Enable camera movement
    }

    // --- State Management ---

    // Cleans up models and resets state before a new generation
    void CleanupPreviousModelsAndPreview()
    {
        if (currentPreviewModelInstance != null) { Destroy(currentPreviewModelInstance); currentPreviewModelInstance = null; }
        if (currentPlacementModelInstance != null) { Destroy(currentPlacementModelInstance); currentPlacementModelInstance = null; }
        lastSuccessfulPreviewTaskId = null;
        isPlacingModel = false;
        originalMaterials.Clear();
        // Don't clear previewImage texture, let it show the last rendered frame or background
    }

    // Helper to set interactable state of all main action buttons
    void SetInteractableStates(bool generate, bool refine, bool place)
    {
        if (generateButton != null) generateButton.interactable = generate;
        if (refineButton != null) refineButton.interactable = refine;
        if (placeButton != null) placeButton.interactable = place;
    }

    // --- Workflow Coroutines ---

    // Handles the Preview task creation, polling, and loading
    IEnumerator StartPreviewGeneration(string prompt)
    {
        SetStatus("Starting Preview generation...");
        lastSuccessfulPreviewTaskId = null;

        string previewTaskId = null;
        yield return StartCoroutine(CreateTaskCoroutine(prompt, isPreview: true, result => previewTaskId = result));
        if (string.IsNullOrEmpty(previewTaskId)) { SetStatus("Failed to create preview task.", true); SetInteractableStates(true, false, false); yield break; } // Re-enable generate only
        SetStatus($"Preview task created ({previewTaskId}). Polling status...");

        TaskStatusResponse previewStatus = null;
        yield return StartCoroutine(PollTaskStatusCoroutine(previewTaskId, result => previewStatus = result, false));
        if (previewStatus == null || previewStatus.status != "SUCCEEDED")
        {
            string errorMsg = previewStatus?.task_error?.message ?? "Polling failed or task did not succeed.";
            SetStatus($"Preview task did not succeed: {errorMsg}", true);
            SetInteractableStates(true, false, false); // Re-enable generate only
            yield break;
        }

        lastSuccessfulPreviewTaskId = previewTaskId;
        SetStatus($"Preview task succeeded ({lastSuccessfulPreviewTaskId}). Loading preview model...");

        string previewModelUrl = previewStatus.model_urls?.glb;
        if (string.IsNullOrEmpty(previewModelUrl))
        {
            SetStatus("Preview succeeded, but no GLB model URL found.", true);
            SetInteractableStates(true, true, false); // Allow generate, allow refine (based on task success), disallow place
            yield break;
        }

        yield return StartCoroutine(LoadModelIntoPreview(previewModelUrl));

        // Final state: Allow generating new, refining this one, placing this one (if loaded)
        SetInteractableStates(true, true, currentPreviewModelInstance != null);
        if (currentPreviewModelInstance != null) { SetStatus($"Preview model loaded ({lastSuccessfulPreviewTaskId}). Ready to Refine, Place, or Rotate."); }
        else { SetStatus($"Preview task succeeded ({lastSuccessfulPreviewTaskId}), but preview GLB loading failed. Ready to Refine.", true); }
    }

    // Handles the Refine task creation, polling, and loading (replaces preview model)
    IEnumerator StartRefineGeneration(string previewTaskIdToRefine) // Input is the PREVIEW task ID
    {
        SetStatus($"Starting Refine task based on {previewTaskIdToRefine}...");

        string newTaskidFromRefine = null; // Variable to store the ID returned by the refine POST request
        yield return StartCoroutine(CreateTaskCoroutine(
            previewTaskIdToRefine,      // Pass the PREVIEW ID as inputData for the refine request body
            isPreview: false,           // Indicate this is a refine request
            result => newTaskidFromRefine = result // Store the NEW task ID returned by the API here
        ));

        // --- CRITICAL CHECK ---
        // Check if the CreateTaskCoroutine successfully returned a task ID
        if (string.IsNullOrEmpty(newTaskidFromRefine))
        {
            // CreateTaskCoroutine already logged detailed errors if it failed.
            SetStatus("Failed to initiate refine task request or get a valid Task ID from response.", true);
            // Restore previous interactable state
            SetInteractableStates(true, !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId), currentPreviewModelInstance != null);
            yield break;
        }
        // --- END CRITICAL CHECK ---

        // Now, use the ID *returned* by the refine request for polling.
        string idToPoll = newTaskidFromRefine;
        SetStatus($"Refine task initiated (Task ID: {idToPoll}). Polling status..."); // Log the ID we are actually polling

        TaskStatusResponse finalStatus = null;
        // Poll THIS new/returned ID, indicating it's for the refine stage
        yield return StartCoroutine(PollTaskStatusCoroutine(idToPoll, result => finalStatus = result, true));

        // --- Check Polling Result ---
        if (finalStatus == null || finalStatus.status != "SUCCEEDED")
        {
            string errorMsg = finalStatus?.task_error?.message ?? "Polling failed or task did not succeed.";
            SetStatus($"Refine task ({idToPoll}) did not succeed: {errorMsg}", true);
            // Restore previous interactable state
            SetInteractableStates(true, !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId), currentPreviewModelInstance != null);
            yield break;
        }
        SetStatus($"Refine task ({idToPoll}) succeeded. Loading refined model...");
        // --- End Check Polling Result ---


        // --- Load Model ---
        // Use the model URL from the *finalStatus* of the polled task (idToPoll)
        string modelUrl = finalStatus.model_urls?.glb;
        if (string.IsNullOrEmpty(modelUrl))
        {
            SetStatus($"Refine task ({idToPoll}) succeeded, but no GLB model URL found in the final status.", true);
            // Still allow generating new, but cannot refine (as it failed technically), can place old preview
            SetInteractableStates(true, false, currentPreviewModelInstance != null);
            yield break;
        }

        // Load the refined model into the preview area
        yield return StartCoroutine(LoadModelIntoPreview(modelUrl));
        // --- End Load Model ---


        // --- Final State ---
        // Allow generating new, cannot refine this refined model further, can place the new refined model (if loaded)
        SetInteractableStates(true, false, currentPreviewModelInstance != null);
        if (currentPreviewModelInstance != null)
        {
            SetStatus($"Refined model loaded (from Task {idToPoll}). Ready to Place or Rotate.");
        }
        else
        {
            // LoadModelIntoPreview handles logging the GLB load failure
            SetStatus($"Refine task ({idToPoll}) succeeded, but refined GLB loading failed.", true);
        }
        // --- End Final State ---
    }

    // --- Core API Interaction Coroutines ---
    IEnumerator CreateTaskCoroutine(string inputData, bool isPreview, System.Action<string> callback)
    {
        string taskType = isPreview ? "preview" : "refine";
        string jsonPayload = "";
        object requestData;

        try // Wrap payload creation in try-catch
        {
            if (isPreview)
            {
                requestData = new TextTo3DRequestPreview { prompt = inputData };
                jsonPayload = JsonUtility.ToJson(requestData);
            }
            else
            {
                requestData = new TextTo3DRequestRefine { preview_task_id = inputData };
                jsonPayload = JsonUtility.ToJson(requestData);
            }
        }
        catch (System.Exception e)
        {
            SetStatus($"Error creating JSON payload for {taskType} task: {e.Message}", true);
            callback?.Invoke(null);
            yield break; // Stop if payload creation fails
        }


        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);

        // --- DETAILED LOGGING ---
        Debug.Log($"[{taskType.ToUpper()}] Sending Request to: {meshyApiUrl}");
        Debug.Log($"[{taskType.ToUpper()}] Authorization Header: Bearer {apiKey?.Substring(0, Math.Min(apiKey.Length, 5))}..."); // Log partial key for verification
        Debug.Log($"[{taskType.ToUpper()}] JSON Payload: {jsonPayload}");
        // --- END DETAILED LOGGING ---

        using (UnityWebRequest request = new UnityWebRequest(meshyApiUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            SetStatus($"Sending {taskType} request...");
            yield return request.SendWebRequest();

            // --- MORE DETAILED LOGGING ---
            Debug.Log($"[{taskType.ToUpper()}] Request Sent. Result: {request.result}, Response Code: {request.responseCode}");
            if (!string.IsNullOrEmpty(request.error))
            {
                Debug.LogError($"[{taskType.ToUpper()}] Request Error: {request.error}");
            }
            string responseJson = request.downloadHandler.text;
            Debug.Log($"[{taskType.ToUpper()}] Raw Response JSON: {responseJson}");
            // --- END MORE DETAILED LOGGING ---


            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    // Try parsing TaskCreateResponse first (expected simple response)
                    TaskCreateResponse response = JsonUtility.FromJson<TaskCreateResponse>(responseJson);
                    if (response != null && !string.IsNullOrEmpty(response.result))
                    {
                        Debug.Log($"[{taskType.ToUpper()}] Successfully parsed TaskCreateResponse. Result ID: {response.result}");
                        callback?.Invoke(response.result);
                    }
                    else
                    {
                        Debug.LogWarning($"[{taskType.ToUpper()}] Response did not contain a 'result' field directly. Attempting to parse as full TaskStatusResponse...");
                        // If no 'result', maybe it sent the full status back immediately?
                        try
                        {
                            TaskStatusResponse fullResponse = JsonUtility.FromJson<TaskStatusResponse>(responseJson);
                            if (fullResponse != null && !string.IsNullOrEmpty(fullResponse.id))
                            {
                                Debug.LogWarning($"[{taskType.ToUpper()}] Parsed full status response. Using ID: {fullResponse.id}");
                                callback?.Invoke(fullResponse.id);
                            }
                            else
                            {
                                throw new System.Exception($"Parsed TaskStatusResponse but no ID found. Original Response: {responseJson}");
                            }
                        }
                        catch (System.Exception parseEx)
                        {
                            SetStatus($"{taskType} task request succeeded, but response parsing failed (no result/ID). Error: {parseEx.Message}", true);
                            Debug.LogError($"Create Task ({taskType}) Response JSON: {responseJson}");
                            callback?.Invoke(null);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    SetStatus($"{taskType} task request succeeded, but response JSON parsing failed: {e.Message}", true);
                    Debug.LogError($"Create Task ({taskType}) Raw Response: {responseJson}");
                    callback?.Invoke(null);
                }
            }
            else
            {
                SetStatus($"Failed to create {taskType} task: HTTP {request.responseCode} {request.error} - {request.downloadHandler.text}", true);
                callback?.Invoke(null);
            }
        }
    }

    IEnumerator PollTaskStatusCoroutine(string taskId, System.Action<TaskStatusResponse> callback, bool isRefinePolling)
    {
        string pollUrl = meshyTaskStatusUrlBase + taskId; float timeWaited = 0f; string stage = isRefinePolling ? "Refine" : "Preview";
        // SetStatus($"Polling ({stage}) status from: {pollUrl}"); // Already logged in SetStatus below
        while (timeWaited < maxPollingTimeSeconds)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(pollUrl))
            {
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}"); yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string responseJson = request.downloadHandler.text; TaskStatusResponse statusResponse = JsonUtility.FromJson<TaskStatusResponse>(responseJson);
                        if (statusResponse.status == "SUCCEEDED") { SetStatus($"{stage} task {taskId} Succeeded! Progress: {statusResponse.progress}%"); callback?.Invoke(statusResponse); yield break; }
                        else if (statusResponse.status == "FAILED") { string errorMsg = statusResponse.task_error?.message ?? "No error message provided."; SetStatus($"{stage} task {taskId} Failed: {errorMsg}", true); callback?.Invoke(statusResponse); yield break; }
                        else if (statusResponse.status == "PENDING" || statusResponse.status == "IN_PROGRESS") { SetStatus($"{stage} task {taskId} Progress: {statusResponse.progress}% (Status: {statusResponse.status})"); }
                        else { SetStatus($"{stage} task {taskId} has unexpected status: {statusResponse.status}. Raw: {responseJson}", true); callback?.Invoke(statusResponse); yield break; }
                    }
                    catch (System.Exception e) { SetStatus($"Polling {stage} task {taskId} failed - JSON Parse Error: {e.Message}. Response: {request.downloadHandler.text}", true); callback?.Invoke(null); yield break; }
                }
                else { SetStatus($"Polling {stage} task {taskId} failed: {request.responseCode} {request.error} - {request.downloadHandler.text}", true); callback?.Invoke(null); yield break; }
            }
            yield return new WaitForSeconds(pollingIntervalSeconds); timeWaited += pollingIntervalSeconds;
        }
        SetStatus($"Polling {stage} task {taskId} timed out after {maxPollingTimeSeconds} seconds.", true); callback?.Invoke(null);
    }

    // --- Model Loading (Using glTFast) ---
    IEnumerator LoadModelIntoPreview(string modelUrl)
    {
        SetStatus($"Loading 3D model into preview: {modelUrl}");

        if (currentPreviewModelInstance != null) { Destroy(currentPreviewModelInstance); currentPreviewModelInstance = null; }
        foreach (Transform child in previewModelContainer.transform) { Destroy(child.gameObject); }

        // Logger - We create it but won't pass it directly to Load/Instantiate based on errors
        var logger = new ConsoleLogger();

        // Initialize GltfImport - Logger *might* be passable here depending on version
        // Check GltfImport constructors in your IDE/docs if unsure.
        // Example: var gltf = new GltfImport(null, null, logger); // If it takes logger
        var gltf = new GltfImport(); // Default constructor

        // Optional: Configure import settings (We won't pass this directly to Load based on error)
        // var importSettings = new ImportSettings { /* ... */ };

        // --- CORRECTED CALL TO gltf.Load ---
        // Error indicates Arg 3 should be CancellationToken. Let's try passing null for Arg 2.
        // Signature might be: Load(string url, ImportSettings settings/IDeferAgent deferAgent, CancellationToken token)
        Task<bool> loadTask = gltf.Load(modelUrl, null, System.Threading.CancellationToken.None); // Line ~440 corrected
                                                                                                  // ^-- Arg 2 is null (placeholder), Arg 3 is Token
        yield return new WaitUntil(() => loadTask.IsCompleted);

        if (loadTask.IsCompletedSuccessfully && loadTask.Result)
        {
            SetStatus("Model data loaded, instantiating scene...");

            // Optional: Configure instantiation settings (We won't pass this directly based on error)
            // var instantiationSettings = new InstantiationSettings { /* ... */ };

            // --- CORRECTED CALL TO gltf.InstantiateMainSceneAsync ---
            // Error indicates Arg 2 should be CancellationToken.
            // Signature likely: InstantiateMainSceneAsync(Transform parent, CancellationToken token)
            Task<bool> instantiateTask = gltf.InstantiateMainSceneAsync(previewModelContainer.transform, System.Threading.CancellationToken.None); // Line ~457 corrected
                                                                                                                                                   // ^-- Arg 2 is Token
            yield return new WaitUntil(() => instantiateTask.IsCompleted);

            if (instantiateTask.IsCompletedSuccessfully && instantiateTask.Result && previewModelContainer.transform.childCount > 0)
            {
                currentPreviewModelInstance = previewModelContainer.transform.GetChild(0).gameObject;
                currentPreviewModelInstance.name = $"PreviewModel_{DateTime.Now:yyyyMMddHHmmss}";

                int targetLayer = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
                if (targetLayer != -1)
                {
                    SetLayerRecursively(currentPreviewModelInstance, targetLayer);
                }
                else
                {
                    Debug.LogError($"Preview Model Layer could not be found! Check layer configuration and Inspector setting.");
                }

                yield return null; // Wait a frame for bounds calculation

                PositionPreviewCamera(currentPreviewModelInstance);
                SetStatus("Model loaded into preview. Ready to Refine, Place, or Rotate.");
                SetInteractableStates(true, !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId), true);
            }
            else
            {
                // Error handling as before...
                string reason = "Unknown error during instantiation.";
                if (instantiateTask.IsFaulted) reason = instantiateTask.Exception?.GetBaseException()?.Message ?? instantiateTask.Exception?.Message ?? "Task Faulted"; // Get inner exception
                else if (!instantiateTask.Result) reason = "InstantiateMainSceneAsync returned false.";
                else if (previewModelContainer.transform.childCount == 0) reason = "No objects were instantiated as children.";
                Debug.LogError($"Scene instantiation failed: {reason}");
                if (instantiateTask.IsFaulted) Debug.LogException(instantiateTask.Exception); // Log full exception details
                SetStatus($"glTF scene instantiation failed: {reason}", true);
                currentPreviewModelInstance = null;
                SetInteractableStates(true, !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId), false);
            }
        }
        else
        {
            // Error handling as before...
            string reason = "Unknown error during loading.";
            if (loadTask.IsFaulted) reason = loadTask.Exception?.GetBaseException()?.Message ?? loadTask.Exception?.Message ?? "Task Faulted"; // Get inner exception
            else if (!loadTask.Result) reason = "Load returned false.";
            Debug.LogError($"Failed to load GLB model data: {reason}");
            if (loadTask.IsFaulted) Debug.LogException(loadTask.Exception); // Log full exception details
            SetStatus($"Failed to load GLB model data: {reason}", true);
            currentPreviewModelInstance = null;
            SetInteractableStates(true, !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId), false);
        }
    }

    // --- Preview Model Helpers ---

    // Positions the preview camera to frame the target model
    void PositionPreviewCamera(GameObject targetModel)
    {
        if (targetModel == null || previewCamera == null) return;
        Bounds bounds = CalculateBounds(targetModel);
        if (bounds.size == Vector3.zero) { Debug.LogWarning("Cannot position preview camera: Bounds zero."); return; }

        float objectSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        float cameraDistance = objectSize * previewPadding * 1.5f;

        // Reset container rotation before setting camera position based on it
        // previewModelContainer.transform.rotation = Quaternion.identity; // Keep rotation for preview

        // Calculate position based on current container rotation but look at bounds center
        Vector3 initialDirection = new Vector3(0, 0.5f, -1); // Adjust initial view angle if needed (slightly up, looking forward)
        Vector3 rotatedDirection = previewModelContainer.transform.rotation * initialDirection.normalized; // Use container's rotation
        // Or keep a fixed camera direction relative to world:
        // Vector3 viewDirection = new Vector3(1f, 0.75f, -1f).normalized; // Southeast-ish view

        Vector3 cameraPositionOffset = rotatedDirection * cameraDistance; // Use container's rotation for offset direction
        Vector3 targetCenter = bounds.center; // Center of the model bounds in world space

        previewCamera.transform.position = targetCenter + cameraPositionOffset;
        previewCamera.transform.LookAt(targetCenter);

        // Adjust camera properties
        if (previewCamera.orthographic) { previewCamera.orthographicSize = objectSize * previewPadding * 0.6f; }
        else { previewCamera.nearClipPlane = Mathf.Max(0.01f, cameraDistance * 0.05f); previewCamera.farClipPlane = cameraDistance * 2.5f; }
    }

    // Calculates the combined bounds of all renderers within a GameObject hierarchy
    Bounds CalculateBounds(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.zero);
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) { bounds.Encapsulate(renderers[i].bounds); }
        return bounds;
    }

    // Recursively sets the layer for a GameObject and all its children
    void SetLayerRecursively(GameObject obj, int newLayer)
    {
        if (obj == null) return;
        obj.layer = newLayer;
        foreach (Transform child in obj.transform)
        {
            if (child == null) continue;
            SetLayerRecursively(child.gameObject, newLayer);
        }
    }

    // --- Preview Rotation Event Handlers ---

    public void OnPreviewPointerDown(PointerEventData eventData)
    {
        if (currentPreviewModelInstance != null) { isDraggingPreview = true; }
    }

    public void OnPreviewDrag(PointerEventData eventData)
    {
        if (isDraggingPreview && currentPreviewModelInstance != null)
        {
            float rotX = eventData.delta.y * previewRotationSpeed * -1;
            float rotY = eventData.delta.x * previewRotationSpeed;
            // Rotate the container, which holds the model, for consistent rotation axis
            previewModelContainer.transform.Rotate(Vector3.up, rotY, Space.World);
            previewModelContainer.transform.Rotate(previewCamera.transform.right, rotX, Space.World);
            // Re-position camera after rotation to keep framing (optional but often looks better)
            // PositionPreviewCamera(currentPreviewModelInstance);
        }
    }

    public void OnPreviewPointerUp(PointerEventData eventData)
    {
        isDraggingPreview = false;
    }

    // --- Placement Logic ---

    // Moves the placement ghost model based on mouse raycast
    void HandlePlacement()
    {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f, placementLayerMask))
        {
            currentPlacementModelInstance.transform.position = hit.point;
            currentPlacementModelInstance.transform.up = hit.normal; // Align to surface
        }
        else
        {
            currentPlacementModelInstance.transform.position = ray.GetPoint(15f); // Default distance
            currentPlacementModelInstance.transform.rotation = Quaternion.identity; // Reset rotation if not hitting
        }
        // Check for confirmation/cancellation clicks
        if (Input.GetMouseButtonDown(0)) FinalizePlacement();
        if (Input.GetMouseButtonDown(1)) CancelPlacement();
    }

    // Finalizes placing the model, makes it opaque, re-enables buttons
    void FinalizePlacement()
    {
        isPlacingModel = false;
        if (currentPlacementModelInstance != null) { SetModelTransparency(currentPlacementModelInstance, false); }
        SetInteractableStates(true, !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId), currentPreviewModelInstance != null); // Restore state
        SetStatus("Model placed. Ready for next action.");
        currentPlacementModelInstance = null; // Release control
        originalMaterials.Clear();

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(true); // Show UI
        isPlacementUIHidden = false;
        if (mainCameraController != null) mainCameraController.SetActive(false); // Disable camera movement
    }

    // Cancels placement, destroys ghost model, re-enables buttons
    void CancelPlacement()
    {
        isPlacingModel = false;
        if (currentPlacementModelInstance != null) { Destroy(currentPlacementModelInstance); currentPlacementModelInstance = null; }
        SetInteractableStates(true, !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId), currentPreviewModelInstance != null); // Restore state
        SetStatus("Placement cancelled.");
        originalMaterials.Clear();

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(true); // Show UI
        isPlacementUIHidden = false;
        if (mainCameraController != null) mainCameraController.SetActive(false); // Disable camera movement
    }

    // Sets transparency by swapping materials
    void SetModelTransparency(GameObject model, bool makeTransparent)
    {
        if (model == null) return;
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
        if (makeTransparent)
        {
            if (transparentMaterial == null) { Debug.LogError("Transparent Material not assigned!"); return; }
            originalMaterials.Clear();
            foreach (Renderer rend in renderers)
            {
                originalMaterials.AddRange(rend.sharedMaterials);
                Material[] transparentMats = new Material[rend.sharedMaterials.Length];
                for (int i = 0; i < transparentMats.Length; ++i) { transparentMats[i] = transparentMaterial; }
                rend.materials = transparentMats; // Use .materials for instancing
            }
        }
        else
        { // Restore original materials
            int materialIndex = 0;
            foreach (Renderer rend in renderers)
            {
                int materialCount = rend.sharedMaterials.Length;
                if (originalMaterials.Count >= materialIndex + materialCount)
                {
                    Material[] originalMats = new Material[materialCount];
                    for (int i = 0; i < materialCount; i++) { originalMats[i] = originalMaterials[materialIndex + i]; }
                    rend.materials = originalMats; // Restore instances
                    materialIndex += materialCount;
                }
                else { Debug.LogWarning($"Not enough cached materials to restore for {rend.name}"); }
            }
            originalMaterials.Clear();
        }
    }
}

// Helper utility for LayerMask conversion (place outside the main class or in a separate file)
public static class LayerMaskUtility
{
    // Converts a LayerMask value (bitmask) to the first layer index it represents
    public static int GetLayerIndexFromMask(LayerMask layerMask)
    {
        int layerMaskValue = layerMask.value;
        if (layerMaskValue == 0) return -1; // No layer selected

        for (int i = 0; i < 32; i++)
        {
            if ((layerMaskValue & (1 << i)) != 0)
            {
                return i; // Return the index of the first layer found in the mask
            }
        }
        return -1; // Should not happen if value is not 0, but included for safety
    }
}