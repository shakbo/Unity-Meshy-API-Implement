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

// --- Removed Windows Speech Recognition using statements ---

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
    // Reference to the voice input button (Now intended for Meta Voice SDK)
    [SerializeField] private Button voiceInputButton; // <--- 在 Inspector 中指定你的語音按鈕
    // Optional reference to the text on the voice button
    [SerializeField] private TextMeshProUGUI voiceButtonText; // <--- (可選) 在 Inspector 中指定按鈕上的文字元件
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

    // --- Removed Windows Speech Recognition internal state variables ---

    // --- JSON Helper Classes (Unchanged from original) ---
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
            if (voiceInputButton != null) voiceInputButton.interactable = false; // Also disable voice button initially
            return;
        }

        SetupPreviewRendering(); // Link Preview Camera to RawImage via RenderTexture

        // Assign button listeners
        generateButton.onClick.AddListener(OnGeneratePreviewClick);
        refineButton.onClick.AddListener(OnRefineClick);
        placeButton.onClick.AddListener(OnPlaceButtonClick);

        // --- Voice Input Button Setup (Now points to Meta Voice SDK Activation) ---
        // The actual listener for voiceInputButton needs to be set in the Inspector
        // to call the Activate() or ToggleActivation() method on the [BuildingBlock] Dictation object.
        // We removed the programmatic listener assignment for Windows Speech here.
        if (voiceInputButton == null)
        {
            Debug.LogWarning("Voice Input Button not assigned in Inspector. Meta Voice SDK cannot be triggered by this button.");
        }
        // --- END VOICE SETUP MODIFICATION ---


        // Set initial button states
        // Voice button interactability might depend on Meta Voice SDK state,
        // but we'll enable it by default if assigned.
        SetInteractableStates(true, false, false, voiceInputButton != null);


        // Setup listeners for preview rotation via code
        SetupEventTriggerListeners();

        SetStatus("Enter a prompt and click Generate Preview, or use Voice Input."); // Updated status
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
        // Voice Input Button is now more important for Meta Quest
        if (voiceInputButton == null) { Debug.LogWarning("Voice Input Button not assigned! Cannot trigger Meta Voice SDK via button."); }
        if (previewImage == null) { Debug.LogError("Preview Image (RawImage) not assigned!"); isValid = false; }
        if (statusText == null) { Debug.LogError("Status Text not assigned!"); isValid = false; }
        if (previewCamera == null) { Debug.LogError("Preview Camera not assigned!"); isValid = false; }
        if (previewModelContainer == null) { Debug.LogError("Preview Model Container not assigned!"); isValid = false; }
        if (transparentMaterial == null) { Debug.LogError("Transparent Material not assigned!"); isValid = false; }
        if (uiPanelToToggle == null) { Debug.LogError("UI Panel To Toggle not assigned!"); isValid = false; }
        if (previewModelLayer == 0) { Debug.LogError("Preview Model Layer is not set in Inspector!"); isValid = false; }
        else // Simplified layer check
        {
            int layerIndex = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
            if (layerIndex == -1 || LayerMask.LayerToName(layerIndex).Length == 0)
            {
                Debug.LogError($"Preview Model Layer selected in Inspector does not exist! Configure it in Project Settings > Tags and Layers.");
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

        previewRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR);
        if (!previewRenderTexture.Create()) { Debug.LogError("Failed to create RenderTexture!"); return; }

        previewCamera.targetTexture = previewRenderTexture;
        previewImage.texture = previewRenderTexture;
        previewImage.color = Color.white;

        int layerIndex = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
        if (layerIndex != -1)
        {
            SetLayerRecursively(previewModelContainer, layerIndex);
            previewCamera.cullingMask = 1 << layerIndex;
        }
        else
        {
            Debug.LogError("Could not determine layer index from PreviewModelLayer mask.");
            previewCamera.cullingMask = 0;
        }
    }

    // Adds listeners to the Event Trigger on the RawImage for drag rotation
    void SetupEventTriggerListeners()
    {
        EventTrigger trigger = previewImage.gameObject.GetComponent<EventTrigger>();
        if (trigger == null) { trigger = previewImage.gameObject.AddComponent<EventTrigger>(); }

        trigger.triggers.Clear();

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
        if (isPlacingModel)
        {
            if (Input.GetKeyDown(uiToggleKey) && uiPanelToToggle != null)
            {
                isPlacementUIHidden = !isPlacementUIHidden;
                uiPanelToToggle.SetActive(!isPlacementUIHidden);
                SetStatus($"Move mouse/camera to position. [{uiToggleKey}] Toggle UI. Left-Click: Place. Right-Click: Cancel.");
            }

            if (currentPlacementModelInstance != null)
            {
                HandlePlacement();
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
            previewRenderTexture.Release();
            Destroy(previewRenderTexture);
        }
        if (currentPreviewModelInstance != null) Destroy(currentPreviewModelInstance);
        if (currentPlacementModelInstance != null) Destroy(currentPlacementModelInstance);

        // --- Removed Windows Speech Cleanup ---
    }

    // Updates the status text UI
    void SetStatus(string message, bool isError = false)
    {
        if (statusText != null) { statusText.text = message; statusText.color = isError ? Color.red : Color.white; }
        if (isError) Debug.LogError(message); else Debug.Log(message);
    }

    // --- Button Click Handlers (Now disable voice button during operations) ---

    void OnGeneratePreviewClick()
    {
        string prompt = promptInput.text;
        if (string.IsNullOrWhiteSpace(prompt)) { SetStatus("Please enter a prompt.", true); return; }
        SetInteractableStates(false, false, false, false); // Disable all buttons
        CleanupPreviousModelsAndPreview();
        StartCoroutine(StartPreviewGeneration(prompt));
    }

    void OnRefineClick()
    {
        if (string.IsNullOrEmpty(lastSuccessfulPreviewTaskId)) { SetStatus("No successful preview available to refine.", true); return; }
        SetInteractableStates(false, false, false, false); // Disable all buttons
        StartCoroutine(StartRefineGeneration(lastSuccessfulPreviewTaskId));
    }

    void OnPlaceButtonClick()
    {
        if (currentPreviewModelInstance == null) { SetStatus("No model available in preview to place.", true); return; }
        if (currentPlacementModelInstance != null) Destroy(currentPlacementModelInstance);

        currentPlacementModelInstance = Instantiate(currentPreviewModelInstance);
        currentPlacementModelInstance.name = "Placement_" + currentPreviewModelInstance.name;
        currentPlacementModelInstance.SetActive(true);

        SetLayerRecursively(currentPlacementModelInstance, LayerMask.NameToLayer("Default"));
        SetModelTransparency(currentPlacementModelInstance, true);

        isPlacingModel = true;
        SetInteractableStates(false, false, false, false); // Disable all buttons during placement
        SetStatus("Move mouse to position, Left-Click to place, Right-Click to cancel.");

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(false);
        isPlacementUIHidden = true;
        if (mainCameraController != null) mainCameraController.SetActive(true);
    }


    // --- Removed Windows Voice Input Logic ---


    // --- State Management ---

    // Cleans up models and resets state before a new generation
    void CleanupPreviousModelsAndPreview()
    {
        if (currentPreviewModelInstance != null) { Destroy(currentPreviewModelInstance); currentPreviewModelInstance = null; }
        if (currentPlacementModelInstance != null) { Destroy(currentPlacementModelInstance); currentPlacementModelInstance = null; }
        lastSuccessfulPreviewTaskId = null;
        isPlacingModel = false;
        originalMaterials.Clear();
    }

    // Helper to set interactable state of all main action buttons
    void SetInteractableStates(bool generate, bool refine, bool place, bool voice)
    {
        if (generateButton != null) generateButton.interactable = generate;
        if (refineButton != null) refineButton.interactable = refine;
        if (placeButton != null) placeButton.interactable = place;
        // Control voice button interactability - assuming it exists
        if (voiceInputButton != null) voiceInputButton.interactable = voice;
    }


    // --- Workflow Coroutines (Now use the 4-parameter SetInteractableStates) ---

    IEnumerator StartPreviewGeneration(string prompt)
    {
        SetStatus("Starting Preview generation...");
        lastSuccessfulPreviewTaskId = null;

        string previewTaskId = null;
        yield return StartCoroutine(CreateTaskCoroutine(prompt, isPreview: true, result => previewTaskId = result));
        if (string.IsNullOrEmpty(previewTaskId))
        {
            SetStatus("Failed to create preview task.", true);
            SetInteractableStates(true, false, false, true); // Re-enable generate & voice
            yield break;
        }
        SetStatus($"Preview task created ({previewTaskId}). Polling status...");

        TaskStatusResponse previewStatus = null;
        yield return StartCoroutine(PollTaskStatusCoroutine(previewTaskId, result => previewStatus = result, false));
        if (previewStatus == null || previewStatus.status != "SUCCEEDED")
        {
            string errorMsg = previewStatus?.task_error?.message ?? "Polling failed or task did not succeed.";
            SetStatus($"Preview task did not succeed: {errorMsg}", true);
            SetInteractableStates(true, false, false, true); // Re-enable generate & voice
            yield break;
        }

        lastSuccessfulPreviewTaskId = previewTaskId;
        SetStatus($"Preview task succeeded ({lastSuccessfulPreviewTaskId}). Loading preview model...");

        string previewModelUrl = previewStatus.model_urls?.glb;
        if (string.IsNullOrEmpty(previewModelUrl))
        {
            SetStatus("Preview succeeded, but no GLB model URL found.", true);
            SetInteractableStates(true, true, false, true); // Allow generate, refine, voice
            yield break;
        }

        yield return StartCoroutine(LoadModelIntoPreview(previewModelUrl));

        bool canPlace = currentPreviewModelInstance != null;
        SetInteractableStates(true, true, canPlace, true); // Allow generate, refine, place (if loaded), voice
        if (currentPreviewModelInstance != null) { SetStatus($"Preview model loaded ({lastSuccessfulPreviewTaskId}). Ready to Refine, Place, or Rotate."); }
        else { SetStatus($"Preview task succeeded ({lastSuccessfulPreviewTaskId}), but preview GLB loading failed. Ready to Refine.", true); }
    }

    IEnumerator StartRefineGeneration(string previewTaskIdToRefine)
    {
        SetStatus($"Starting Refine task based on {previewTaskIdToRefine}...");

        string newTaskidFromRefine = null;
        yield return StartCoroutine(CreateTaskCoroutine(
            previewTaskIdToRefine,
            isPreview: false,
            result => newTaskidFromRefine = result
        ));

        if (string.IsNullOrEmpty(newTaskidFromRefine))
        {
            SetStatus("Failed to initiate refine task request or get a valid Task ID from response.", true);
            bool canPlace = currentPreviewModelInstance != null;
            bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
            SetInteractableStates(true, canRefine, canPlace, true); // Restore previous state
            yield break;
        }

        string idToPoll = newTaskidFromRefine;
        SetStatus($"Refine task initiated (Task ID: {idToPoll}). Polling status...");

        TaskStatusResponse finalStatus = null;
        yield return StartCoroutine(PollTaskStatusCoroutine(idToPoll, result => finalStatus = result, true));

        if (finalStatus == null || finalStatus.status != "SUCCEEDED")
        {
            string errorMsg = finalStatus?.task_error?.message ?? "Polling failed or task did not succeed.";
            SetStatus($"Refine task ({idToPoll}) did not succeed: {errorMsg}", true);
            bool canPlace = currentPreviewModelInstance != null;
            bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
            SetInteractableStates(true, canRefine, canPlace, true); // Restore previous state
            yield break;
        }
        SetStatus($"Refine task ({idToPoll}) succeeded. Loading refined model...");

        string modelUrl = finalStatus.model_urls?.glb;
        if (string.IsNullOrEmpty(modelUrl))
        {
            SetStatus($"Refine task ({idToPoll}) succeeded, but no GLB model URL found in the final status.", true);
            bool canPlace = currentPreviewModelInstance != null;
            SetInteractableStates(true, false, canPlace, true); // Cannot refine refined, allow place old preview
            yield break;
        }

        yield return StartCoroutine(LoadModelIntoPreview(modelUrl));

        bool refinedLoaded = currentPreviewModelInstance != null;
        SetInteractableStates(true, false, refinedLoaded, true); // Allow generate, cannot refine, place refined, voice
        if (currentPreviewModelInstance != null)
        {
            SetStatus($"Refined model loaded (from Task {idToPoll}). Ready to Place or Rotate.");
        }
        else
        {
            SetStatus($"Refine task ({idToPoll}) succeeded, but refined GLB loading failed.", true);
        }
    }

    // --- Core API Interaction Coroutines (Unchanged) ---
    IEnumerator CreateTaskCoroutine(string inputData, bool isPreview, System.Action<string> callback)
    {
        string taskType = isPreview ? "preview" : "refine";
        string jsonPayload = "";
        object requestData;

        try
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
            yield break;
        }

        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        Debug.Log($"[{taskType.ToUpper()}] Sending Request to: {meshyApiUrl}");
        Debug.Log($"[{taskType.ToUpper()}] Authorization Header: Bearer {apiKey?.Substring(0, Math.Min(apiKey.Length, 5))}...");
        Debug.Log($"[{taskType.ToUpper()}] JSON Payload: {jsonPayload}");

        using (UnityWebRequest request = new UnityWebRequest(meshyApiUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            SetStatus($"Sending {taskType} request...");
            yield return request.SendWebRequest();

            Debug.Log($"[{taskType.ToUpper()}] Request Sent. Result: {request.result}, Response Code: {request.responseCode}");
            if (!string.IsNullOrEmpty(request.error)) Debug.LogError($"[{taskType.ToUpper()}] Request Error: {request.error}");
            string responseJson = request.downloadHandler.text;
            Debug.Log($"[{taskType.ToUpper()}] Raw Response JSON: {responseJson}");

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    TaskCreateResponse response = JsonUtility.FromJson<TaskCreateResponse>(responseJson);
                    if (response != null && !string.IsNullOrEmpty(response.result))
                    {
                        Debug.Log($"[{taskType.ToUpper()}] Successfully parsed TaskCreateResponse. Result ID: {response.result}");
                        callback?.Invoke(response.result);
                    }
                    else
                    {
                        Debug.LogWarning($"[{taskType.ToUpper()}] Response did not contain 'result'. Trying full status...");
                        try
                        {
                            TaskStatusResponse fullResponse = JsonUtility.FromJson<TaskStatusResponse>(responseJson);
                            if (fullResponse != null && !string.IsNullOrEmpty(fullResponse.id))
                            {
                                Debug.LogWarning($"[{taskType.ToUpper()}] Parsed full status response. Using ID: {fullResponse.id}");
                                callback?.Invoke(fullResponse.id);
                            }
                            else throw new System.Exception($"Parsed TaskStatusResponse but no ID. Original: {responseJson}");
                        }
                        catch (System.Exception parseEx)
                        {
                            SetStatus($"{taskType} task OK, but response parse failed (no result/ID). Error: {parseEx.Message}", true);
                            Debug.LogError($"Create Task ({taskType}) JSON: {responseJson}");
                            callback?.Invoke(null);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    SetStatus($"{taskType} task OK, but JSON parse failed: {e.Message}", true);
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
        string pollUrl = meshyTaskStatusUrlBase + taskId;
        float timeWaited = 0f;
        string stage = isRefinePolling ? "Refine" : "Preview";
        while (timeWaited < maxPollingTimeSeconds)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(pollUrl))
            {
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string responseJson = request.downloadHandler.text;
                        TaskStatusResponse statusResponse = JsonUtility.FromJson<TaskStatusResponse>(responseJson);
                        if (statusResponse.status == "SUCCEEDED") { SetStatus($"{stage} task {taskId} Succeeded! Progress: {statusResponse.progress}%"); callback?.Invoke(statusResponse); yield break; }
                        else if (statusResponse.status == "FAILED") { string errorMsg = statusResponse.task_error?.message ?? "No error msg."; SetStatus($"{stage} task {taskId} Failed: {errorMsg}", true); callback?.Invoke(statusResponse); yield break; }
                        else if (statusResponse.status == "PENDING" || statusResponse.status == "IN_PROGRESS") { SetStatus($"{stage} task {taskId} Progress: {statusResponse.progress}% (Status: {statusResponse.status})"); }
                        else { SetStatus($"{stage} task {taskId} unexpected status: {statusResponse.status}. Raw: {responseJson}", true); callback?.Invoke(statusResponse); yield break; }
                    }
                    catch (System.Exception e) { SetStatus($"Polling {stage} task {taskId} failed - JSON Parse Error: {e.Message}. Response: {request.downloadHandler.text}", true); callback?.Invoke(null); yield break; }
                }
                else { SetStatus($"Polling {stage} task {taskId} failed: {request.responseCode} {request.error} - {request.downloadHandler.text}", true); callback?.Invoke(null); yield break; }
            }
            yield return new WaitForSeconds(pollingIntervalSeconds);
            timeWaited += pollingIntervalSeconds;
        }
        SetStatus($"Polling {stage} task {taskId} timed out after {maxPollingTimeSeconds} seconds.", true);
        callback?.Invoke(null);
    }

    // --- Model Loading (Using glTFast - Unchanged) ---
    IEnumerator LoadModelIntoPreview(string modelUrl)
    {
        SetStatus($"Loading 3D model into preview: {modelUrl}");

        if (currentPreviewModelInstance != null) { Destroy(currentPreviewModelInstance); currentPreviewModelInstance = null; }
        foreach (Transform child in previewModelContainer.transform) { Destroy(child.gameObject); }

        var gltf = new GltfImport();
        Task<bool> loadTask = gltf.Load(modelUrl, null, System.Threading.CancellationToken.None);
        yield return new WaitUntil(() => loadTask.IsCompleted);

        if (loadTask.IsCompletedSuccessfully && loadTask.Result)
        {
            SetStatus("Model data loaded, instantiating scene...");
            Task<bool> instantiateTask = gltf.InstantiateMainSceneAsync(previewModelContainer.transform, System.Threading.CancellationToken.None);
            yield return new WaitUntil(() => instantiateTask.IsCompleted);

            if (instantiateTask.IsCompletedSuccessfully && instantiateTask.Result && previewModelContainer.transform.childCount > 0)
            {
                currentPreviewModelInstance = previewModelContainer.transform.GetChild(0).gameObject;
                currentPreviewModelInstance.name = $"PreviewModel_{DateTime.Now:yyyyMMddHHmmss}";

                int targetLayer = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
                if (targetLayer != -1) SetLayerRecursively(currentPreviewModelInstance, targetLayer);
                else Debug.LogError($"Preview Model Layer could not be found!");

                yield return null; // Wait a frame for bounds calculation
                PositionPreviewCamera(currentPreviewModelInstance);

                bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
                SetInteractableStates(true, canRefine, true, true); // Enable Place
                SetStatus("Model loaded into preview. Ready to Refine, Place, or Rotate.");
            }
            else
            {
                string reason = "Unknown error during instantiation.";
                if (instantiateTask.IsFaulted) reason = instantiateTask.Exception?.GetBaseException()?.Message ?? "Task Faulted";
                else if (!instantiateTask.Result) reason = "InstantiateMainSceneAsync returned false.";
                else if (previewModelContainer.transform.childCount == 0) reason = "No objects instantiated.";
                Debug.LogError($"Scene instantiation failed: {reason}");
                if (instantiateTask.IsFaulted) Debug.LogException(instantiateTask.Exception);
                SetStatus($"glTF scene instantiation failed: {reason}", true);
                currentPreviewModelInstance = null;
                bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
                SetInteractableStates(true, canRefine, false, true); // Cannot place
            }
        }
        else
        {
            string reason = "Unknown error during loading.";
            if (loadTask.IsFaulted) reason = loadTask.Exception?.GetBaseException()?.Message ?? "Task Faulted";
            else if (!loadTask.Result) reason = "Load returned false.";
            Debug.LogError($"Failed to load GLB model data: {reason}");
            if (loadTask.IsFaulted) Debug.LogException(loadTask.Exception);
            SetStatus($"Failed to load GLB model data: {reason}", true);
            currentPreviewModelInstance = null;
            bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
            SetInteractableStates(true, canRefine, false, true); // Cannot place
        }
    }

    // --- Preview Model Helpers (Unchanged) ---

    void PositionPreviewCamera(GameObject targetModel)
    {
        if (targetModel == null || previewCamera == null) return;
        Bounds bounds = CalculateBounds(targetModel);
        if (bounds.size == Vector3.zero) { Debug.LogWarning("Cannot position preview camera: Bounds zero."); return; }

        float objectSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        float cameraDistance = objectSize * previewPadding * 1.5f;

        Vector3 initialDirection = new Vector3(0, 0.5f, -1);
        Vector3 rotatedDirection = previewModelContainer.transform.rotation * initialDirection.normalized;
        Vector3 cameraPositionOffset = rotatedDirection * cameraDistance;
        Vector3 targetCenter = bounds.center;

        previewCamera.transform.position = targetCenter + cameraPositionOffset;
        previewCamera.transform.LookAt(targetCenter);

        if (previewCamera.orthographic) { previewCamera.orthographicSize = objectSize * previewPadding * 0.6f; }
        else { previewCamera.nearClipPlane = Mathf.Max(0.01f, cameraDistance * 0.05f); previewCamera.farClipPlane = cameraDistance * 2.5f; }
    }

    Bounds CalculateBounds(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.zero);
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) { bounds.Encapsulate(renderers[i].bounds); }
        return bounds;
    }

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

    // --- Preview Rotation Event Handlers (Unchanged) ---

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
            previewModelContainer.transform.Rotate(Vector3.up, rotY, Space.World);
            previewModelContainer.transform.Rotate(previewCamera.transform.right, rotX, Space.World);
        }
    }

    public void OnPreviewPointerUp(PointerEventData eventData)
    {
        isDraggingPreview = false;
    }

    // --- Placement Logic (Now uses the 4-parameter SetInteractableStates) ---

    void HandlePlacement()
    {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f, placementLayerMask))
        {
            currentPlacementModelInstance.transform.position = hit.point;
            currentPlacementModelInstance.transform.up = hit.normal;
        }
        else
        {
            currentPlacementModelInstance.transform.position = ray.GetPoint(15f);
            currentPlacementModelInstance.transform.rotation = Quaternion.identity;
        }
        if (Input.GetMouseButtonDown(0)) FinalizePlacement();
        if (Input.GetMouseButtonDown(1)) CancelPlacement();
    }

    void FinalizePlacement()
    {
        isPlacingModel = false;
        if (currentPlacementModelInstance != null) { SetModelTransparency(currentPlacementModelInstance, false); }

        bool canPlace = currentPreviewModelInstance != null;
        bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
        SetInteractableStates(true, canRefine, canPlace, true); // Restore state including voice
        SetStatus("Model placed. Ready for next action.");
        currentPlacementModelInstance = null;
        originalMaterials.Clear();

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(true);
        isPlacementUIHidden = false;
        if (mainCameraController != null) mainCameraController.SetActive(false);
    }

    void CancelPlacement()
    {
        isPlacingModel = false;
        if (currentPlacementModelInstance != null) { Destroy(currentPlacementModelInstance); currentPlacementModelInstance = null; }

        bool canPlace = currentPreviewModelInstance != null;
        bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
        SetInteractableStates(true, canRefine, canPlace, true); // Restore state including voice
        SetStatus("Placement cancelled.");
        originalMaterials.Clear();

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(true);
        isPlacementUIHidden = false;
        if (mainCameraController != null) mainCameraController.SetActive(false);
    }

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
                rend.materials = transparentMats;
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
                    for (int i = 0; i < materialCount; i++)
                    {
                        if (materialIndex + i < originalMaterials.Count)
                            originalMats[i] = originalMaterials[materialIndex + i];
                        else Debug.LogWarning($"Missing original material at index {materialIndex + i} for {rend.name}");
                    }
                    rend.materials = originalMats;
                    materialIndex += materialCount;
                }
                else { Debug.LogWarning($"Not enough cached materials for {rend.name}"); }
            }
            originalMaterials.Clear();
        }
    }

    // --- ADD THIS FUNCTION TO RECEIVE META VOICE SDK RESULTS ---
    public void UpdatePromptFromMetaDictation(string dictatedText)
    {
        if (promptInput != null)
        {
            promptInput.text = dictatedText; // 將辨識結果設定到輸入框
            Debug.Log($"Meta Dictation Result Received: {dictatedText}"); // 在 Console 輸出結果，方便除錯
            // 你也可以選擇在這裡更新狀態文字
            // SetStatus($"Voice input received: {dictatedText.Substring(0, Math.Min(dictatedText.Length, 30))}...");
        }
        else
        {
            Debug.LogError("MeshyController Error: Prompt Input field is not assigned!");
        }
    }
    // --- END ADDED FUNCTION ---
}

// Helper utility for LayerMask conversion (Unchanged)
public static class LayerMaskUtility
{
    public static int GetLayerIndexFromMask(LayerMask layerMask)
    {
        int layerMaskValue = layerMask.value;
        if (layerMaskValue == 0) return -1;

        for (int i = 0; i < 32; i++)
        {
            if ((layerMaskValue & (1 << i)) != 0)
            {
                return i;
            }
        }
        return -1;
    }
}