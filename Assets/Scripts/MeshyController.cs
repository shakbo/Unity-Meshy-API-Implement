using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Collections;
using System.Text;
using TMPro; // Required for TextMeshPro
using System.Collections.Generic;
using System;
using UnityEngine.EventSystems; // Required for Event Trigger system

// Requires glTFast package
using GLTFast;
// using GLTFast.Logging; // Optional, for ConsoleLogger
using System.Threading.Tasks; // Required for Task-based operations

// For XR Interaction Toolkit
using UnityEngine.XR.Interaction.Toolkit;
// For new Input System Actions
using UnityEngine.InputSystem;
// For Casters
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
// using UnityEngine.XR.Interaction.Toolkit.Interactors; // NearFarInteractor is in Toolkit, not specifically Interactors sub-namespace for using statement


public class MeshyController : MonoBehaviour
{
    [Header("API Configuration")]
    [SerializeField] private string apiKey = "YOUR_MESHY_API_KEY";
    [SerializeField] private string meshyApiUrl = "https://api.meshy.ai/openapi/v2/text-to-3d";
    [SerializeField] private string meshyTaskStatusUrlBase = "https://api.meshy.ai/openapi/v2/text-to-3d/";
    [SerializeField] private float pollingIntervalSeconds = 5.0f;
    [SerializeField] private float maxPollingTimeSeconds = 600f;

    [Header("UI Elements")]
    [SerializeField] private TMP_InputField promptInput;
    [SerializeField] private Button generateButton;
    [SerializeField] private Button refineButton;
    [SerializeField] private Button placeButton;
    [SerializeField] private Button voiceInputButton;
    [SerializeField] private TextMeshProUGUI voiceButtonText;
    [SerializeField] private RawImage previewImage;
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Preview Setup")]
    [SerializeField] private Camera previewCamera;
    [SerializeField] private GameObject previewModelContainer;
    [SerializeField] private LayerMask previewModelLayer;
    [SerializeField] private float previewPadding = 1.2f;
    [SerializeField] private float previewRotationSpeed = 0.4f;
    [Tooltip("Factor to scale the preview model by. e.g., 0.1 for 10% size.")]
    [SerializeField] private float previewModelScaleFactor = 0.5f; // Default scale factor

    [Header("Placement Setup")]
    [SerializeField] private LayerMask placementLayerMask = 1;
    [SerializeField] private Material transparentMaterial;
    [SerializeField] private GameObject uiPanelToToggle;
    [SerializeField] private NearFarInteractor placementInteractor;
    [SerializeField] private SphereInteractionCaster placementSphereCaster;

    [Header("VR Input Actions (Assign from Input Action Asset)")]
    [SerializeField] private InputActionReference confirmPlacementAction;
    [SerializeField] private InputActionReference cancelPlacementAction;


    // Internal State
    private GameObject currentPreviewModelInstance;
    private GameObject currentPlacementModelInstance;
    private RenderTexture previewRenderTexture;
    private bool isPlacingModel = false;
    private List<Material> originalMaterials = new List<Material>();
    private string lastSuccessfulPreviewTaskId = null;
    private bool isDraggingPreview = false;
    private bool isPlacementUIHidden = false; // Correctly manage this state
    private RaycastHit[] m_SphereCastHits = new RaycastHit[10];

    // --- JSON Helper Classes (Unchanged) ---
    [System.Serializable] private class TextTo3DRequestPreview { public string mode = "preview"; public string prompt; public string art_style = "realistic"; public bool should_remesh = true; public int target_polycount = 30000; }
    [System.Serializable] private class TextTo3DRequestRefine { public string mode = "refine"; public string preview_task_id; public bool enable_pbr = true; }
    [System.Serializable] private class TaskCreateResponse { public string result; }
    [System.Serializable] private class TaskStatusResponse { public string id; public ModelUrls model_urls; public string thumbnail_url; public string prompt; public string art_style; public int progress; public long started_at; public long created_at; public long finished_at; public string status; public List<TextureInfo> texture_urls; public int preceding_tasks; public TaskError task_error; }
    [System.Serializable] private class ModelUrls { public string glb; public string fbx; public string obj; public string mtl; public string usdz; }
    [System.Serializable] private class TextureInfo { public string base_color; }
    [System.Serializable] private class TaskError { public string code; public string message; }
    // --- End JSON Helper Classes ---

    void Start()
    {
        if (!ValidateConfiguration())
        {
            SetInteractableStates(false, false, false, false);
            return;
        }
        SetupPreviewRendering();
        generateButton.onClick.AddListener(OnGeneratePreviewClick);
        refineButton.onClick.AddListener(OnRefineClick);
        placeButton.onClick.AddListener(OnPlaceButtonClick);
        if (voiceInputButton == null) { Debug.LogWarning("Voice Input Button not assigned."); }
        SetInteractableStates(true, false, false, voiceInputButton != null);
        SetupEventTriggerListeners();
        SetStatus("Enter a prompt and click Generate Preview, or use Voice Input.");
    }

    bool ValidateConfiguration()
    {
        bool isValid = true;
        // ... (all other existing null checks remain the same) ...
        if (string.IsNullOrEmpty(apiKey) || apiKey == "YOUR_MESHY_API_KEY") { SetStatus("Error: API Key not set!", true); isValid = false; }
        if (promptInput == null) { Debug.LogError("Prompt Input (TMP_InputField) not assigned!"); isValid = false; }
        if (generateButton == null) { Debug.LogError("Generate Button not assigned!"); isValid = false; }
        if (refineButton == null) { Debug.LogError("Refine Button not assigned!"); isValid = false; }
        if (placeButton == null) { Debug.LogError("Place Button not assigned!"); isValid = false; }
        if (previewImage == null) { Debug.LogError("Preview Image (RawImage) not assigned!"); isValid = false; }
        if (statusText == null) { Debug.LogError("Status Text (TextMeshProUGUI) not assigned!"); isValid = false; }
        if (previewCamera == null) { Debug.LogError("Preview Camera not assigned!"); isValid = false; }
        if (previewModelContainer == null) { Debug.LogError("Preview Model Container not assigned!"); isValid = false; }
        if (transparentMaterial == null) { Debug.LogError("Transparent Material not assigned!"); isValid = false; }
        if (uiPanelToToggle == null) { Debug.LogError("UI Panel To Toggle not assigned!"); isValid = false; }
        if (previewModelLayer == 0) { Debug.LogError("Preview Model Layer is not set!"); isValid = false; }
        else { int layerIndex = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer); if (layerIndex == -1 || LayerMask.LayerToName(layerIndex).Length == 0) { Debug.LogError("Preview Model Layer in Inspector does not exist!"); isValid = false; } }

        if (placementInteractor == null) { Debug.LogError("Placement Interactor (NearFarInteractor) not assigned! Fallback positioning might be affected."); isValid = false; }
        if (placementSphereCaster == null) { Debug.LogError("Placement Sphere Caster not assigned! Model placement will not work correctly."); isValid = false; }
        if (confirmPlacementAction == null || confirmPlacementAction.action == null) { Debug.LogWarning("Confirm Placement Action not assigned."); }
        if (cancelPlacementAction == null || cancelPlacementAction.action == null) { Debug.LogWarning("Cancel Placement Action not assigned."); }
        return isValid;
    }

    void SetupPreviewRendering() { /* ... (Same as before) ... */ if (previewRenderTexture != null) { /* Cleanup */ } RectTransform rawImageRect = previewImage.GetComponent<RectTransform>(); int width = Mathf.Max(1, (int)rawImageRect.rect.width); int height = Mathf.Max(1, (int)rawImageRect.rect.height); previewRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR); if (!previewRenderTexture.Create()) { Debug.LogError("Failed to create RenderTexture!"); return; } previewCamera.targetTexture = previewRenderTexture; previewImage.texture = previewRenderTexture; previewImage.color = Color.white; int layerIndex = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer); if (layerIndex != -1) { SetLayerRecursively(previewModelContainer, layerIndex); previewCamera.cullingMask = 1 << layerIndex; } else { Debug.LogError("Could not determine layer index from PreviewModelLayer mask."); previewCamera.cullingMask = 0; } }
    void SetupEventTriggerListeners() { /* ... (Same as before) ... */ EventTrigger trigger = previewImage.gameObject.GetComponent<EventTrigger>(); if (trigger == null) { trigger = previewImage.gameObject.AddComponent<EventTrigger>(); } trigger.triggers.Clear(); AddEventTriggerListener(trigger, EventTriggerType.PointerDown, (data) => { OnPreviewPointerDown((PointerEventData)data); }); AddEventTriggerListener(trigger, EventTriggerType.Drag, (data) => { OnPreviewDrag((PointerEventData)data); }); AddEventTriggerListener(trigger, EventTriggerType.PointerUp, (data) => { OnPreviewPointerUp((PointerEventData)data); }); }
    void AddEventTriggerListener(EventTrigger trigger, EventTriggerType eventType, UnityEngine.Events.UnityAction<BaseEventData> action) { /* ... (Same as before) ... */ EventTrigger.Entry entry = new EventTrigger.Entry { eventID = eventType }; entry.callback.AddListener(action); trigger.triggers.Add(entry); }

    void Update()
    {
        if (isPlacingModel)
        {
            HandlePlacement();
        }
    }

    void OnDestroy() { /* ... (Same as before) ... */ if (previewRenderTexture != null) { if (previewCamera != null) previewCamera.targetTexture = null; if (previewImage != null) previewImage.texture = null; previewRenderTexture.Release(); Destroy(previewRenderTexture); } if (currentPreviewModelInstance != null) Destroy(currentPreviewModelInstance); if (currentPlacementModelInstance != null) Destroy(currentPlacementModelInstance); }
    void SetStatus(string message, bool isError = false) { /* ... (Same as before) ... */ if (statusText != null) { statusText.text = message; statusText.color = isError ? Color.red : Color.white; } if (isError) Debug.LogError(message); else Debug.Log(message); }
    void OnGeneratePreviewClick() { /* ... (Same as before) ... */ string prompt = promptInput.text; if (string.IsNullOrWhiteSpace(prompt)) { SetStatus("Please enter a prompt.", true); return; } SetInteractableStates(false, false, false, false); CleanupPreviousModelsAndPreview(); StartCoroutine(StartPreviewGeneration(prompt)); }
    void OnRefineClick() { /* ... (Same as before) ... */ if (string.IsNullOrEmpty(lastSuccessfulPreviewTaskId)) { SetStatus("No successful preview to refine.", true); return; } SetInteractableStates(false, false, false, false); StartCoroutine(StartRefineGeneration(lastSuccessfulPreviewTaskId)); }

    // --- MODIFIED OnPlaceButtonClick ---
    void OnPlaceButtonClick()
    {
        if (currentPreviewModelInstance == null) { SetStatus("No model in preview to place.", true); return; }
        if (currentPlacementModelInstance != null) Destroy(currentPlacementModelInstance);

        currentPlacementModelInstance = Instantiate(currentPreviewModelInstance);
        currentPlacementModelInstance.name = "Placement_" + currentPreviewModelInstance.name;
        // currentPlacementModelInstance.transform.localScale will be inherited from currentPreviewModelInstance

        currentPlacementModelInstance.SetActive(true);
        SetLayerRecursively(currentPlacementModelInstance, LayerMask.NameToLayer("Default")); // Or your desired final placement layer
        SetModelTransparency(currentPlacementModelInstance, true); // Make it a ghost

        isPlacingModel = true;
        SetInteractableStates(false, false, false, false); // Disable buttons during placement
        SetStatus("Move controller. Confirm/Cancel with assigned buttons.");

        if (uiPanelToToggle != null)
        {
            uiPanelToToggle.SetActive(false);
            isPlacementUIHidden = true; // <<--- SET isPlacementUIHidden to true
            Debug.Log("UI Panel hidden for placement.");
        }
    }

    void CleanupPreviousModelsAndPreview() { /* ... (Same as before) ... */ if (currentPreviewModelInstance != null) { Destroy(currentPreviewModelInstance); currentPreviewModelInstance = null; } if (currentPlacementModelInstance != null) { Destroy(currentPlacementModelInstance); currentPlacementModelInstance = null; } lastSuccessfulPreviewTaskId = null; isPlacingModel = false; originalMaterials.Clear(); }
    void SetInteractableStates(bool generate, bool refine, bool place, bool voice) { /* ... (Same as before) ... */ if (generateButton != null) generateButton.interactable = generate; if (refineButton != null) refineButton.interactable = refine; if (placeButton != null) placeButton.interactable = place; if (voiceInputButton != null) voiceInputButton.interactable = voice; }

    // --- Workflow Coroutines (Largely Unchanged) ---
    IEnumerator StartPreviewGeneration(string prompt) { /* ... (Same as before) ... */ SetStatus("Starting Preview generation..."); lastSuccessfulPreviewTaskId = null; string previewTaskId = null; yield return StartCoroutine(CreateTaskCoroutine(prompt, isPreview: true, result => previewTaskId = result)); if (string.IsNullOrEmpty(previewTaskId)) { SetStatus("Failed to create preview task.", true); SetInteractableStates(true, false, false, true); yield break; } SetStatus($"Preview task created ({previewTaskId}). Polling status..."); TaskStatusResponse previewStatus = null; yield return StartCoroutine(PollTaskStatusCoroutine(previewTaskId, result => previewStatus = result, false)); if (previewStatus == null || previewStatus.status != "SUCCEEDED") { string errorMsg = previewStatus?.task_error?.message ?? "Polling failed or task did not succeed."; SetStatus($"Preview task did not succeed: {errorMsg}", true); SetInteractableStates(true, false, false, true); yield break; } lastSuccessfulPreviewTaskId = previewTaskId; SetStatus($"Preview task succeeded ({lastSuccessfulPreviewTaskId}). Loading model..."); string previewModelUrl = previewStatus.model_urls?.glb; if (string.IsNullOrEmpty(previewModelUrl)) { SetStatus("Preview succeeded, but no GLB URL found.", true); SetInteractableStates(true, true, false, true); yield break; } yield return StartCoroutine(LoadModelIntoPreview(previewModelUrl)); bool canPlace = currentPreviewModelInstance != null; SetInteractableStates(true, true, canPlace, true); if (currentPreviewModelInstance != null) { SetStatus($"Preview model loaded ({lastSuccessfulPreviewTaskId}). Ready to Refine, Place, or Rotate."); } else { SetStatus($"Preview task succeeded ({lastSuccessfulPreviewTaskId}), but GLB loading failed. Ready to Refine.", true); } }
    IEnumerator StartRefineGeneration(string previewTaskIdToRefine) { /* ... (Same as before) ... */ SetStatus($"Starting Refine task based on {previewTaskIdToRefine}..."); string newTaskidFromRefine = null; yield return StartCoroutine(CreateTaskCoroutine(previewTaskIdToRefine, isPreview: false, result => newTaskidFromRefine = result)); if (string.IsNullOrEmpty(newTaskidFromRefine)) { SetStatus("Failed to initiate refine task.", true); bool canPlaceOld = currentPreviewModelInstance != null; bool canRefineOld = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId); SetInteractableStates(true, canRefineOld, canPlaceOld, true); yield break; } string idToPoll = newTaskidFromRefine; SetStatus($"Refine task initiated (ID: {idToPoll}). Polling status..."); TaskStatusResponse finalStatus = null; yield return StartCoroutine(PollTaskStatusCoroutine(idToPoll, result => finalStatus = result, true)); if (finalStatus == null || finalStatus.status != "SUCCEEDED") { string errorMsg = finalStatus?.task_error?.message ?? "Polling failed or task did not succeed."; SetStatus($"Refine task ({idToPoll}) did not succeed: {errorMsg}", true); bool canPlaceOld = currentPreviewModelInstance != null; bool canRefineOld = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId); SetInteractableStates(true, canRefineOld, canPlaceOld, true); yield break; } SetStatus($"Refine task ({idToPoll}) succeeded. Loading refined model..."); string modelUrl = finalStatus.model_urls?.glb; if (string.IsNullOrEmpty(modelUrl)) { SetStatus($"Refine task ({idToPoll}) succeeded, but no GLB URL.", true); bool canPlaceOld = currentPreviewModelInstance != null; SetInteractableStates(true, false, canPlaceOld, true); yield break; } yield return StartCoroutine(LoadModelIntoPreview(modelUrl)); bool refinedLoaded = currentPreviewModelInstance != null; SetInteractableStates(true, false, refinedLoaded, true); if (currentPreviewModelInstance != null) { SetStatus($"Refined model loaded (Task {idToPoll}). Ready to Place or Rotate."); } else { SetStatus($"Refine task ({idToPoll}) succeeded, but refined GLB loading failed.", true); } }
    IEnumerator CreateTaskCoroutine(string inputData, bool isPreview, System.Action<string> callback) { /* ... (Same as before) ... */ string taskType = isPreview ? "preview" : "refine"; string jsonPayload = ""; object requestData; try { if (isPreview) { requestData = new TextTo3DRequestPreview { prompt = inputData }; jsonPayload = JsonUtility.ToJson(requestData); } else { requestData = new TextTo3DRequestRefine { preview_task_id = inputData }; jsonPayload = JsonUtility.ToJson(requestData); } } catch (System.Exception e) { SetStatus($"Error creating JSON for {taskType}: {e.Message}", true); callback?.Invoke(null); yield break; } byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload); Debug.Log($"[{taskType.ToUpper()}] Sending to: {meshyApiUrl}, Auth: Bearer {apiKey?.Substring(0, Math.Min(apiKey.Length, 5))}..., Payload: {jsonPayload}"); using (UnityWebRequest request = new UnityWebRequest(meshyApiUrl, "POST")) { request.uploadHandler = new UploadHandlerRaw(bodyRaw); request.downloadHandler = new DownloadHandlerBuffer(); request.SetRequestHeader("Content-Type", "application/json"); request.SetRequestHeader("Authorization", $"Bearer {apiKey}"); SetStatus($"Sending {taskType} request..."); yield return request.SendWebRequest(); Debug.Log($"[{taskType.ToUpper()}] Sent. Result: {request.result}, Code: {request.responseCode}. Error: {request.error}. Response: {request.downloadHandler.text}"); if (request.result == UnityWebRequest.Result.Success) { try { TaskCreateResponse response = JsonUtility.FromJson<TaskCreateResponse>(request.downloadHandler.text); if (response != null && !string.IsNullOrEmpty(response.result)) { callback?.Invoke(response.result); } else { TaskStatusResponse fullResponse = JsonUtility.FromJson<TaskStatusResponse>(request.downloadHandler.text); if (fullResponse != null && !string.IsNullOrEmpty(fullResponse.id)) { callback?.Invoke(fullResponse.id); } else throw new System.Exception("No result/ID."); } } catch (System.Exception e) { SetStatus($"{taskType} OK, but JSON parse failed: {e.Message}. Raw: {request.downloadHandler.text}", true); callback?.Invoke(null); } } else { SetStatus($"Failed to create {taskType}: HTTP {request.responseCode} {request.error} - {request.downloadHandler.text}", true); callback?.Invoke(null); } } }
    IEnumerator PollTaskStatusCoroutine(string taskId, System.Action<TaskStatusResponse> callback, bool isRefinePolling) { /* ... (Same as before) ... */ string pollUrl = meshyTaskStatusUrlBase + taskId; float timeWaited = 0f; string stage = isRefinePolling ? "Refine" : "Preview"; while (timeWaited < maxPollingTimeSeconds) { using (UnityWebRequest request = UnityWebRequest.Get(pollUrl)) { request.SetRequestHeader("Authorization", $"Bearer {apiKey}"); yield return request.SendWebRequest(); if (request.result == UnityWebRequest.Result.Success) { try { string responseJson = request.downloadHandler.text; TaskStatusResponse statusResponse = JsonUtility.FromJson<TaskStatusResponse>(responseJson); if (statusResponse.status == "SUCCEEDED") { SetStatus($"{stage} task {taskId} Succeeded! Progress: {statusResponse.progress}%"); callback?.Invoke(statusResponse); yield break; } else if (statusResponse.status == "FAILED") { string errorMsg = statusResponse.task_error?.message ?? "No error."; SetStatus($"{stage} task {taskId} Failed: {errorMsg}", true); callback?.Invoke(statusResponse); yield break; } else if (statusResponse.status == "PENDING" || statusResponse.status == "IN_PROGRESS") { SetStatus($"{stage} task {taskId} Progress: {statusResponse.progress}% (Status: {statusResponse.status})"); } else { SetStatus($"{stage} task {taskId} unexpected: {statusResponse.status}. Raw: {responseJson}", true); callback?.Invoke(statusResponse); yield break; } } catch (System.Exception e) { SetStatus($"Polling {stage} {taskId} - JSON Parse Error: {e.Message}. Response: {request.downloadHandler.text}", true); callback?.Invoke(null); yield break; } } else { SetStatus($"Polling {stage} {taskId} failed: {request.responseCode} {request.error} - {request.downloadHandler.text}", true); callback?.Invoke(null); yield break; } } yield return new WaitForSeconds(pollingIntervalSeconds); timeWaited += pollingIntervalSeconds; } SetStatus($"Polling {stage} {taskId} timed out.", true); callback?.Invoke(null); }

    // --- MODIFIED LoadModelIntoPreview with Scaling ---
    IEnumerator LoadModelIntoPreview(string modelUrl)
    {
        SetStatus($"Loading 3D model: {modelUrl}");
        if (currentPreviewModelInstance != null) { Destroy(currentPreviewModelInstance); currentPreviewModelInstance = null; }
        foreach (Transform child in previewModelContainer.transform) { Destroy(child.gameObject); }

        var gltf = new GltfImport();
        Task<bool> loadTask = gltf.Load(modelUrl);
        yield return new WaitUntil(() => loadTask.IsCompleted);

        if (loadTask.IsCompletedSuccessfully && loadTask.Result)
        {
            SetStatus("Model data loaded, instantiating scene...");
            Task<bool> instantiateTask = gltf.InstantiateMainSceneAsync(previewModelContainer.transform);
            yield return new WaitUntil(() => instantiateTask.IsCompleted);

            if (instantiateTask.IsCompletedSuccessfully && instantiateTask.Result && previewModelContainer.transform.childCount > 0)
            {
                currentPreviewModelInstance = previewModelContainer.transform.GetChild(0).gameObject;
                currentPreviewModelInstance.name = $"PreviewModel_{DateTime.Now:yyyyMMddHHmmss}";

                // Apply scaling based on the Inspector field
                currentPreviewModelInstance.transform.localScale = Vector3.one * previewModelScaleFactor;
                Debug.Log($"Preview model '{currentPreviewModelInstance.name}' scaled by a factor of {previewModelScaleFactor}");

                int targetLayer = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
                if (targetLayer != -1) SetLayerRecursively(currentPreviewModelInstance, targetLayer);
                else Debug.LogError($"Preview Model Layer could not be found for {currentPreviewModelInstance.name}!");

                yield return null;
                PositionPreviewCamera(currentPreviewModelInstance);

                bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
                SetInteractableStates(true, canRefine, true, true);
                SetStatus("Model loaded into preview. Ready to Refine, Place, or Rotate.");
            }
            else { /* ... (Same error handling as before) ... */ string reason = "Unknown error during instantiation."; if (instantiateTask.IsFaulted) reason = instantiateTask.Exception?.GetBaseException()?.Message ?? "Task Faulted during instantiation"; else if (!instantiateTask.Result) reason = "InstantiateMainSceneAsync returned false."; else if (previewModelContainer.transform.childCount == 0) reason = "No objects were instantiated into the preview container."; Debug.LogError($"Scene instantiation failed: {reason}"); if (instantiateTask.IsFaulted) Debug.LogException(instantiateTask.Exception); SetStatus($"glTF scene instantiation failed: {reason}", true); currentPreviewModelInstance = null; bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId); SetInteractableStates(true, canRefine, false, true); }
        }
        else { /* ... (Same error handling as before) ... */ string reason = "Unknown error during model data loading."; if (loadTask.IsFaulted) reason = loadTask.Exception?.GetBaseException()?.Message ?? "Task Faulted during loading"; else if (!loadTask.Result) reason = "gltf.Load returned false."; Debug.LogError($"Failed to load GLB model data from URL '{modelUrl}': {reason}"); if (loadTask.IsFaulted) Debug.LogException(loadTask.Exception); SetStatus($"Failed to load GLB model data: {reason}", true); currentPreviewModelInstance = null; bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId); SetInteractableStates(true, canRefine, false, true); }
    }

    // --- Preview Model Helpers (Unchanged) ---
    void PositionPreviewCamera(GameObject targetModel) { /* ... (Same as before) ... */ if (targetModel == null || previewCamera == null) return; Bounds bounds = CalculateBounds(targetModel); if (bounds.size == Vector3.zero) return; float objectSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z); float cameraDistance = objectSize * previewPadding * 1.5f; Vector3 initialDirection = new Vector3(0, 0.5f, -1); Vector3 rotatedDirection = previewModelContainer.transform.rotation * initialDirection.normalized; Vector3 cameraPositionOffset = rotatedDirection * cameraDistance; Vector3 targetCenter = bounds.center; previewCamera.transform.position = targetCenter + cameraPositionOffset; previewCamera.transform.LookAt(targetCenter); if (previewCamera.orthographic) { previewCamera.orthographicSize = objectSize * previewPadding * 0.6f; } else { previewCamera.nearClipPlane = Mathf.Max(0.01f, cameraDistance * 0.05f); previewCamera.farClipPlane = cameraDistance * 2.5f; } }
    Bounds CalculateBounds(GameObject obj) { /* ... (Same as before) ... */ Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(); if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.zero); Bounds bounds = renderers[0].bounds; for (int i = 1; i < renderers.Length; i++) { bounds.Encapsulate(renderers[i].bounds); } return bounds; }
    void SetLayerRecursively(GameObject obj, int newLayer) { /* ... (Same as before) ... */ if (obj == null) return; obj.layer = newLayer; foreach (Transform child in obj.transform) { if (child == null) continue; SetLayerRecursively(child.gameObject, newLayer); } }

    // --- Preview Rotation Event Handlers (Unchanged) ---
    public void OnPreviewPointerDown(PointerEventData eventData) { /* ... (Same as before) ... */ if (currentPreviewModelInstance != null) { isDraggingPreview = true; } }
    public void OnPreviewDrag(PointerEventData eventData) { /* ... (Same as before) ... */ if (isDraggingPreview && currentPreviewModelInstance != null) { float rotX = eventData.delta.y * previewRotationSpeed * -1; float rotY = eventData.delta.x * previewRotationSpeed; previewModelContainer.transform.Rotate(Vector3.up, rotY, Space.World); previewModelContainer.transform.Rotate(previewCamera.transform.right, rotX, Space.World); } }
    public void OnPreviewPointerUp(PointerEventData eventData) { /* ... (Same as before) ... */ isDraggingPreview = false; }


    // --- MODIFIED VR Placement Logic (HandlePlacement) ---
    void HandlePlacement()
    {
        if (currentPlacementModelInstance == null) return;

        if (placementSphereCaster == null || !placementSphereCaster.enabled)
        {
            if (placementInteractor != null) { Transform controllerTransform = placementInteractor.transform; currentPlacementModelInstance.transform.position = controllerTransform.position + controllerTransform.forward * 1.5f; currentPlacementModelInstance.transform.rotation = Quaternion.LookRotation(controllerTransform.forward, controllerTransform.up); if (placementSphereCaster == null) SetStatus("Placement Sphere Caster not assigned. Using fallback.", true); else SetStatus("Placement Sphere Caster disabled. Using fallback.", true); }
            else { SetStatus("Placement Interactor & Caster not assigned. Cannot position.", true); }
            HandlePlacementInput();
            return;
        }

        bool hasValidHit = false;
        RaycastHit bestHitInfo = new RaycastHit();
        float closestHitDistance = float.MaxValue;

        Transform effectiveOrigin = placementSphereCaster.effectiveCastOrigin;
        if (effectiveOrigin == null) effectiveOrigin = placementSphereCaster.transform;

        Vector3 rayOrigin = effectiveOrigin.position;
        Vector3 rayDirection = effectiveOrigin.forward;
        float radius = placementSphereCaster.castRadius;
        LayerMask casterPhysicsMask = placementSphereCaster.physicsLayerMask;
        QueryTriggerInteraction triggerInteraction = placementSphereCaster.physicsTriggerInteraction;

        int numHits = Physics.SphereCastNonAlloc(rayOrigin, radius, rayDirection, m_SphereCastHits, 100f, casterPhysicsMask, triggerInteraction);

        if (numHits > 0)
        {
            for (int i = 0; i < numHits; i++)
            {
                RaycastHit currentHit = m_SphereCastHits[i];
                if (currentHit.collider != null && (((1 << currentHit.collider.gameObject.layer) & placementLayerMask) != 0))
                {
                    if (currentHit.distance < closestHitDistance) { closestHitDistance = currentHit.distance; bestHitInfo = currentHit; hasValidHit = true; }
                }
            }
        }

        if (hasValidHit) { currentPlacementModelInstance.transform.position = bestHitInfo.point; currentPlacementModelInstance.transform.up = bestHitInfo.normal; }
        else { if (placementInteractor != null) { Transform controllerTransform = placementInteractor.transform; currentPlacementModelInstance.transform.position = controllerTransform.position + controllerTransform.forward * 1.5f; currentPlacementModelInstance.transform.rotation = Quaternion.LookRotation(controllerTransform.forward, controllerTransform.up); } }
        HandlePlacementInput();
    }

    void HandlePlacementInput()
    {
        if (confirmPlacementAction != null && confirmPlacementAction.action != null && confirmPlacementAction.action.WasPressedThisFrame())
        {
            FinalizePlacement();
        }
        else if (cancelPlacementAction != null && cancelPlacementAction.action != null && cancelPlacementAction.action.WasPressedThisFrame())
        {
            CancelPlacement();
        }
    }

    // --- MODIFIED FinalizePlacement ---
    void FinalizePlacement()
    {
        isPlacingModel = false;
        if (currentPlacementModelInstance != null) { SetModelTransparency(currentPlacementModelInstance, false); }
        bool canPlace = currentPreviewModelInstance != null;
        bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
        SetInteractableStates(true, canRefine, canPlace, true);
        SetStatus("Model placed. Ready for next action.");
        // currentPlacementModelInstance = null; // Keep the instance if you want it to stay in scene
        originalMaterials.Clear();

        if (uiPanelToToggle != null && isPlacementUIHidden)
        {
            uiPanelToToggle.SetActive(true);
            isPlacementUIHidden = false; // <<--- RESET isPlacementUIHidden to false
            Debug.Log("UI Panel shown after placement finalized.");
        }
    }

    // --- MODIFIED CancelPlacement ---
    void CancelPlacement()
    {
        isPlacingModel = false;
        if (currentPlacementModelInstance != null) { Destroy(currentPlacementModelInstance); currentPlacementModelInstance = null; }
        bool canPlace = currentPreviewModelInstance != null;
        bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
        SetInteractableStates(true, canRefine, canPlace, true);
        SetStatus("Placement cancelled.");
        originalMaterials.Clear();

        if (uiPanelToToggle != null && isPlacementUIHidden)
        {
            uiPanelToToggle.SetActive(true);
            isPlacementUIHidden = false; // <<--- RESET isPlacementUIHidden to false
            Debug.Log("UI Panel shown after placement cancelled.");
        }
    }

    void SetModelTransparency(GameObject model, bool makeTransparent) { /* ... (Same as before) ... */ if (model == null) return; Renderer[] renderers = model.GetComponentsInChildren<Renderer>(); if (makeTransparent) { if (transparentMaterial == null) { Debug.LogError("Transparent Material not assigned!"); return; } originalMaterials.Clear(); foreach (Renderer rend in renderers) { originalMaterials.AddRange(rend.sharedMaterials); Material[] transparentMats = new Material[rend.sharedMaterials.Length]; for (int i = 0; i < transparentMats.Length; ++i) { transparentMats[i] = transparentMaterial; } rend.materials = transparentMats; } } else { int materialIndex = 0; foreach (Renderer rend in renderers) { int materialCount = rend.sharedMaterials.Length; if (originalMaterials.Count >= materialIndex + materialCount) { Material[] originalMats = new Material[materialCount]; for (int i = 0; i < materialCount; i++) { if (materialIndex + i < originalMaterials.Count) originalMats[i] = originalMaterials[materialIndex + i]; else Debug.LogWarning($"Missing original material at index {materialIndex + i} for {rend.name}"); } rend.materials = originalMats; materialIndex += materialCount; } else { Debug.LogWarning($"Not enough cached materials for {rend.name}"); } } originalMaterials.Clear(); } }

    public void UpdatePromptFromMetaDictation(string dictatedText) { /* ... (Same as before) ... */ if (promptInput != null) { promptInput.text = dictatedText; Debug.Log($"Meta Dictation Result: {dictatedText}"); } else { Debug.LogError("MeshyController: Prompt Input field not assigned!"); } }
}

// LayerMaskUtility (Unchanged)
public static class LayerMaskUtility
{
    public static int GetLayerIndexFromMask(LayerMask layerMask) { /* ... (Same as before) ... */ int layerMaskValue = layerMask.value; if (layerMaskValue == 0) return -1; for (int i = 0; i < 32; i++) { if ((layerMaskValue & (1 << i)) != 0) { return i; } } return -1; }
}