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

// --- ADD THIS FOR WINDOWS SPEECH RECOGNITION ---
#if UNITY_STANDALONE_WIN || UNITY_WSA
using UnityEngine.Windows.Speech;
using System.Linq; // Required for KeywordRecognizer/DictationRecognizer setup
#endif
// --- END ADDITION ---

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
    // --- ADD THIS ---
    [SerializeField] private Button voiceInputButton; // <--- 在 Inspector 中指定你的語音按鈕
    [SerializeField] private TextMeshProUGUI voiceButtonText; // <--- (可選) 在 Inspector 中指定按鈕上的文字元件
    // --- END ADDITION ---
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
    private string initialVoiceButtonText;

    // --- ADD THIS FOR VOICE RECOGNITION ---
#if UNITY_STANDALONE_WIN || UNITY_WSA
    private DictationRecognizer dictationRecognizer;
    private bool isListening = false;
    private string initialVoiceButtonText = "🎙️"; // 儲存按鈕初始文字/圖標
#endif
    // --- END ADDITION ---

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
            if (voiceInputButton != null) voiceInputButton.interactable = false; // 同步禁用語音按鈕
            return;
        }

        SetupPreviewRendering(); // Link Preview Camera to RawImage via RenderTexture

        // Assign button listeners
        generateButton.onClick.AddListener(OnGeneratePreviewClick);
        refineButton.onClick.AddListener(OnRefineClick);
        placeButton.onClick.AddListener(OnPlaceButtonClick);

        // --- ADD THIS ---
        if (voiceInputButton != null)
        {
            if (voiceButtonText != null) initialVoiceButtonText = voiceButtonText.text; // 儲存初始文字
            voiceInputButton.onClick.AddListener(ToggleVoiceInput); // 為語音按鈕添加監聽器
            SetupVoiceRecognition(); // 初始化語音辨識
        }
        else
        {
            Debug.LogWarning("Voice Input Button not assigned in Inspector.");
        }
        // --- END ADDITION ---


        // Set initial button states
        generateButton.interactable = true;
        refineButton.interactable = false;
        placeButton.interactable = false;

        // Setup listeners for preview rotation via code
        SetupEventTriggerListeners();

        SetStatus("Enter a prompt and click Generate Preview, or use Voice Input (Win).");
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
        // --- ADD THIS CHECK ---
        // 語音按鈕不是核心功能，只給警告
        if (voiceInputButton == null) { Debug.LogWarning("Voice Input Button not assigned!"); }
        // --- END ADDITION ---
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

        // --- ADD THIS ---
        CleanupVoiceRecognition(); // 清理語音辨識資源
        // --- END ADDITION ---
    }

    // Updates the status text UI
    void SetStatus(string message, bool isError = false)
    {
        if (statusText != null) { statusText.text = message; statusText.color = isError ? Color.red : Color.white; }
        if (isError) Debug.LogError(message); else Debug.Log(message);
    }

    // --- Button Click Handlers (Unchanged) ---

    void OnGeneratePreviewClick()
    {
        string prompt = promptInput.text;
        if (string.IsNullOrWhiteSpace(prompt)) { SetStatus("Please enter a prompt.", true); return; }
        SetInteractableStates(false, false, false, false); // Disable buttons (including voice)
        CleanupPreviousModelsAndPreview();
        StartCoroutine(StartPreviewGeneration(prompt));
    }

    void OnRefineClick()
    {
        if (string.IsNullOrEmpty(lastSuccessfulPreviewTaskId)) { SetStatus("No successful preview available to refine.", true); return; }
        SetInteractableStates(false, false, false, false); // Disable buttons (including voice)
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
        SetInteractableStates(false, false, false, false); // Disable buttons during placement (including voice)
        SetStatus("Move mouse to position, Left-Click to place, Right-Click to cancel.");

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(false); // Hide UI initially
        isPlacementUIHidden = true; // Track state
        if (mainCameraController != null) mainCameraController.SetActive(true); // Enable camera movement
    }


    // --- Voice Input Logic ---

#if UNITY_STANDALONE_WIN || UNITY_WSA
    void SetupVoiceRecognition()
    {
        try
        {
            // 檢查系統是否支援語音辨識
            if (PhraseRecognitionSystem.isSupported)
            {
                // 使用 DictationRecognizer 進行自由格式的語音輸入
                dictationRecognizer = new DictationRecognizer();

                // 訂閱事件
                dictationRecognizer.DictationResult += HandleDictationResult;       // 最終辨識結果
                dictationRecognizer.DictationHypothesis += HandleDictationHypothesis; // 辨識過程中的假設(部分結果)
                dictationRecognizer.DictationComplete += HandleDictationComplete;   // 辨識完成 (不論成功、失敗、超時)
                dictationRecognizer.DictationError += HandleDictationError;         // 辨識發生錯誤

                if (voiceInputButton != null) voiceInputButton.interactable = true; // 啟用語音按鈕
                SetStatus("Voice recognition ready (Windows).");
                Debug.Log("Dictation Recognizer initialized.");
            }
            else
            {
                SetStatus("Voice recognition not supported on this system.", true);
                Debug.LogError("PhraseRecognitionSystem not supported.");
                if (voiceInputButton != null) voiceInputButton.interactable = false; // 禁用語音按鈕
            }
        }
        catch (Exception e)
        {
            SetStatus($"Error initializing voice recognition: {e.Message}", true);
            Debug.LogException(e);
            if (voiceInputButton != null) voiceInputButton.interactable = false; // 初始化失敗也禁用
        }
    }

    void ToggleVoiceInput()
    {
        if (dictationRecognizer == null)
        {
            SetStatus("Voice recognizer not initialized.", true);
            return;
        }

        if (isListening) // 如果正在監聽，則停止
        {
            StopListening();
        }
        else // 否則，開始監聽
        {
            StartListening();
        }
    }

    void StartListening()
    {
        // 避免重複啟動或在非就緒狀態下啟動
        if (isListening || dictationRecognizer == null || dictationRecognizer.Status == SpeechSystemStatus.Running)
        {
            Debug.LogWarning("Already listening or recognizer not ready/running.");
            return;
        }

        // 檢查是否有可用的麥克風
        if (Microphone.devices.Length == 0)
        {
            SetStatus("No microphone detected!", true);
            Debug.LogError("No microphone detected. Cannot start listening.");
            return;
        }

        dictationRecognizer.Start(); // 啟動辨識器
        isListening = true;
        SetStatus("Listening... Speak your prompt.");
        if (voiceButtonText != null) voiceButtonText.text = "Listening..."; // 更新按鈕文字提示
        // 可選：改變按鈕顏色等視覺提示
        // if (voiceInputButton != null) { /* Change button appearance */ }
        Debug.Log("DictationRecognizer started.");
    }

    void StopListening()
    {
        // 避免在未監聽或非運行狀態下停止
        if (!isListening || dictationRecognizer == null || dictationRecognizer.Status != SpeechSystemStatus.Running)
        {
            Debug.LogWarning("Not listening or recognizer not ready/not running.");
            return;
        }

        dictationRecognizer.Stop(); // 停止辨識器
        Debug.Log("DictationRecognizer stopped by user.");
        // isListening 會在 HandleDictationComplete 中被設為 false
        // 按鈕文字也會在 HandleDictationComplete 中被重設
    }

    // 處理最終辨識結果
    private void HandleDictationResult(string text, ConfidenceLevel confidence)
    {
        Debug.Log($"Dictation Result: '{text}' (Confidence: {confidence})");
        if (!string.IsNullOrEmpty(text))
        {
            // --- 核心：將辨識結果填入輸入框 ---
            promptInput.text = text;
            // --- ------------------------- ---
            SetStatus($"Voice input received: {text.Substring(0, Math.Min(text.Length, 30))}..."); // 在狀態欄顯示部分結果
        }
        // 收到結果後通常會自動觸發 DictationComplete，不需要在這裡手動 StopListening()
    }

    // 處理辨識過程中的部分結果 (可選，用於即時反饋)
    private void HandleDictationHypothesis(string text)
    {
        // Debug.Log($"Dictation Hypothesis: {text}");
        SetStatus($"Listening... (Heard: {text.Substring(0, Math.Min(text.Length, 30))}...)"); // 狀態欄顯示部分聽到的內容
        // 如果希望即時更新輸入框 (可能會有些干擾):
        // promptInput.text = text;
    }

    // 處理辨識完成事件 (無論原因)
    private void HandleDictationComplete(DictationCompletionCause cause)
    {
        isListening = false; // 重設監聽狀態
        SetStatus($"Voice recognition stopped. Reason: {cause}"); // 顯示停止原因
        if (voiceButtonText != null) voiceButtonText.text = initialVoiceButtonText; // 恢復按鈕文字
        // 可選：恢復按鈕顏色等視覺提示
        // if (voiceInputButton != null) { /* Reset button appearance */ }

        // 檢查是否是因為錯誤或超時而停止
        if (cause != DictationCompletionCause.Complete)
        {
            Debug.LogWarning($"Dictation completed unexpectedly: {cause}");
            // 這裡可以考慮是否需要重新初始化辨識器，以防後續出錯
            // CleanupVoiceRecognition();
            // SetupVoiceRecognition();
        }
        else
        {
            Debug.Log("Dictation completed successfully.");
        }
    }

    // 處理辨識錯誤事件
    private void HandleDictationError(string error, int hresult)
    {
        isListening = false; // 重設監聽狀態
        SetStatus($"Voice recognition error: {error} (HRESULT: {hresult})", true);
        Debug.LogError($"Dictation Error: {error}\nHRESULT: {hresult}");
        if (voiceButtonText != null) voiceButtonText.text = initialVoiceButtonText; // 恢復按鈕文字
        // 可選：禁用按鈕或改變外觀提示錯誤
        // if (voiceInputButton != null) { voiceInputButton.interactable = false; /* Reset appearance */ }

        // 發生錯誤後，可能需要清理並重新初始化
        // CleanupVoiceRecognition();
        // SetupVoiceRecognition();
    }

    // 清理語音辨識相關資源
    void CleanupVoiceRecognition()
    {
        if (dictationRecognizer != null)
        {
            // 取消訂閱事件，防止內存洩漏
            dictationRecognizer.DictationResult -= HandleDictationResult;
            dictationRecognizer.DictationHypothesis -= HandleDictationHypothesis;
            dictationRecognizer.DictationComplete -= HandleDictationComplete;
            dictationRecognizer.DictationError -= HandleDictationError;

            // 如果辨識器仍在運行，則停止它
            if (dictationRecognizer.Status == SpeechSystemStatus.Running)
            {
                dictationRecognizer.Stop();
                Debug.Log("Stopped running DictationRecognizer during cleanup.");
            }
            dictationRecognizer.Dispose(); // 釋放資源
            dictationRecognizer = null;
            Debug.Log("DictationRecognizer disposed.");
        }
        isListening = false; // 確保狀態被重設
    }

#else
    // 為非 Windows 平台提供提示或禁用功能
    void SetupVoiceRecognition()
    {
        Debug.LogWarning("Voice recognition via UnityEngine.Windows.Speech is only available on Windows Standalone/UWP builds.");
        if (voiceInputButton != null)
        {
            voiceInputButton.interactable = false; // 禁用按鈕
            if (voiceButtonText != null) voiceButtonText.text = "N/A"; // 修改按鈕文字
        }
        SetStatus("Voice input unavailable on this platform.");
    }

    void ToggleVoiceInput()
    {
        SetStatus("Voice input unavailable on this platform.", true);
    }

     void CleanupVoiceRecognition()
     {
         // 在非 Windows 平台，此實現無需清理
     }

#endif

    // --- End Voice Input Logic ---


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
    // Overload to include voice button state
    void SetInteractableStates(bool generate, bool refine, bool place, bool voice)
    {
        if (generateButton != null) generateButton.interactable = generate;
        if (refineButton != null) refineButton.interactable = refine;
        if (placeButton != null) placeButton.interactable = place;
#if UNITY_STANDALONE_WIN || UNITY_WSA // Only control voice button on supported platforms
        if (voiceInputButton != null) voiceInputButton.interactable = voice && (dictationRecognizer != null); // Also check if recognizer is ready
#else
        if (voiceInputButton != null) voiceInputButton.interactable = false; // Always disabled on other platforms
#endif
    }
    // Original overload (calls the new one, assuming voice should be enabled if others are)
    void SetInteractableStates(bool generate, bool refine, bool place)
    {
        // Default: if generate is enabled, voice should also be potentially enabled
        // Placement state might disable voice too. Refine state depends on preview.
        bool enableVoice = generate && !isPlacingModel;
        SetInteractableStates(generate, refine, place, enableVoice);
    }


    // --- Workflow Coroutines (Unchanged, but check SetInteractableStates calls) ---

    // Handles the Preview task creation, polling, and loading
    IEnumerator StartPreviewGeneration(string prompt)
    {
        SetStatus("Starting Preview generation...");
        lastSuccessfulPreviewTaskId = null;

        string previewTaskId = null;
        yield return StartCoroutine(CreateTaskCoroutine(prompt, isPreview: true, result => previewTaskId = result));
        if (string.IsNullOrEmpty(previewTaskId))
        {
            SetStatus("Failed to create preview task.", true);
            SetInteractableStates(true, false, false, true); // Re-enable generate & voice only
            yield break;
        }
        SetStatus($"Preview task created ({previewTaskId}). Polling status...");

        TaskStatusResponse previewStatus = null;
        yield return StartCoroutine(PollTaskStatusCoroutine(previewTaskId, result => previewStatus = result, false));
        if (previewStatus == null || previewStatus.status != "SUCCEEDED")
        {
            string errorMsg = previewStatus?.task_error?.message ?? "Polling failed or task did not succeed.";
            SetStatus($"Preview task did not succeed: {errorMsg}", true);
            SetInteractableStates(true, false, false, true); // Re-enable generate & voice only
            yield break;
        }

        lastSuccessfulPreviewTaskId = previewTaskId;
        SetStatus($"Preview task succeeded ({lastSuccessfulPreviewTaskId}). Loading preview model...");

        string previewModelUrl = previewStatus.model_urls?.glb;
        if (string.IsNullOrEmpty(previewModelUrl))
        {
            SetStatus("Preview succeeded, but no GLB model URL found.", true);
            // Allow generate, allow refine (based on task success), disallow place, allow voice
            SetInteractableStates(true, true, false, true);
            yield break;
        }

        yield return StartCoroutine(LoadModelIntoPreview(previewModelUrl));

        // Final state: Allow generating new, refining this one, placing this one (if loaded), allow voice
        bool canPlace = currentPreviewModelInstance != null;
        SetInteractableStates(true, true, canPlace, true);
        if (currentPreviewModelInstance != null) { SetStatus($"Preview model loaded ({lastSuccessfulPreviewTaskId}). Ready to Refine, Place, or Rotate."); }
        else { SetStatus($"Preview task succeeded ({lastSuccessfulPreviewTaskId}), but preview GLB loading failed. Ready to Refine.", true); }
    }

    // Handles the Refine task creation, polling, and loading (replaces preview model)
    IEnumerator StartRefineGeneration(string previewTaskIdToRefine) // Input is the PREVIEW task ID
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
            // Restore previous interactable state (including voice)
            bool canPlace = currentPreviewModelInstance != null;
            bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
            SetInteractableStates(true, canRefine, canPlace, true);
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
            // Restore previous interactable state (including voice)
            bool canPlace = currentPreviewModelInstance != null;
            bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
            SetInteractableStates(true, canRefine, canPlace, true);
            yield break;
        }
        SetStatus($"Refine task ({idToPoll}) succeeded. Loading refined model...");

        string modelUrl = finalStatus.model_urls?.glb;
        if (string.IsNullOrEmpty(modelUrl))
        {
            SetStatus($"Refine task ({idToPoll}) succeeded, but no GLB model URL found in the final status.", true);
            // Allow generate, cannot refine refined, can place old preview, allow voice
            bool canPlace = currentPreviewModelInstance != null;
            SetInteractableStates(true, false, canPlace, true);
            yield break;
        }

        yield return StartCoroutine(LoadModelIntoPreview(modelUrl));

        // Final State: Allow generate, cannot refine further, can place refined, allow voice
        bool refinedLoaded = currentPreviewModelInstance != null;
        SetInteractableStates(true, false, refinedLoaded, true);
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
                    TaskCreateResponse response = JsonUtility.FromJson<TaskCreateResponse>(responseJson);
                    if (response != null && !string.IsNullOrEmpty(response.result))
                    {
                        Debug.Log($"[{taskType.ToUpper()}] Successfully parsed TaskCreateResponse. Result ID: {response.result}");
                        callback?.Invoke(response.result);
                    }
                    else
                    {
                        Debug.LogWarning($"[{taskType.ToUpper()}] Response did not contain a 'result' field directly. Attempting to parse as full TaskStatusResponse...");
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

    // --- Model Loading (Using glTFast - Unchanged, assuming corrections were applied previously) ---
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
                // Corrected state update after successful load
                bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId); // Check if there *was* a preview task
                SetInteractableStates(true, canRefine, true, true); // Enable Place now, potentially enable refine, enable voice
                SetStatus("Model loaded into preview. Ready to Refine, Place, or Rotate.");
            }
            else
            {
                string reason = "Unknown error during instantiation.";
                if (instantiateTask.IsFaulted) reason = instantiateTask.Exception?.GetBaseException()?.Message ?? instantiateTask.Exception?.Message ?? "Task Faulted";
                else if (!instantiateTask.Result) reason = "InstantiateMainSceneAsync returned false.";
                else if (previewModelContainer.transform.childCount == 0) reason = "No objects were instantiated as children.";
                Debug.LogError($"Scene instantiation failed: {reason}");
                if (instantiateTask.IsFaulted) Debug.LogException(instantiateTask.Exception);
                SetStatus($"glTF scene instantiation failed: {reason}", true);
                currentPreviewModelInstance = null;
                // Corrected state update after instantiation fail
                bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
                SetInteractableStates(true, canRefine, false, true); // Cannot place, potentially can refine, enable voice
            }
        }
        else
        {
            string reason = "Unknown error during loading.";
            if (loadTask.IsFaulted) reason = loadTask.Exception?.GetBaseException()?.Message ?? loadTask.Exception?.Message ?? "Task Faulted";
            else if (!loadTask.Result) reason = "Load returned false.";
            Debug.LogError($"Failed to load GLB model data: {reason}");
            if (loadTask.IsFaulted) Debug.LogException(loadTask.Exception);
            SetStatus($"Failed to load GLB model data: {reason}", true);
            currentPreviewModelInstance = null;
            // Corrected state update after load fail
            bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
            SetInteractableStates(true, canRefine, false, true); // Cannot place, potentially can refine, enable voice
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

    // --- Placement Logic (Unchanged, but check SetInteractableStates calls) ---

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
        if (Input.GetMouseButtonDown(0)) FinalizePlacement();
        if (Input.GetMouseButtonDown(1)) CancelPlacement();
    }

    void FinalizePlacement()
    {
        isPlacingModel = false;
        if (currentPlacementModelInstance != null) { SetModelTransparency(currentPlacementModelInstance, false); }
        // Restore state (including voice)
        bool canPlace = currentPreviewModelInstance != null; // Should still reference the preview model
        bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
        SetInteractableStates(true, canRefine, canPlace, true);
        SetStatus("Model placed. Ready for next action.");
        currentPlacementModelInstance = null; // Release control
        originalMaterials.Clear();

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(true); // Show UI
        isPlacementUIHidden = false;
        if (mainCameraController != null) mainCameraController.SetActive(false); // Disable camera movement
    }

    void CancelPlacement()
    {
        isPlacingModel = false;
        if (currentPlacementModelInstance != null) { Destroy(currentPlacementModelInstance); currentPlacementModelInstance = null; }
        // Restore state (including voice)
        bool canPlace = currentPreviewModelInstance != null;
        bool canRefine = !string.IsNullOrEmpty(lastSuccessfulPreviewTaskId);
        SetInteractableStates(true, canRefine, canPlace, true);
        SetStatus("Placement cancelled.");
        originalMaterials.Clear();

        if (uiPanelToToggle != null) uiPanelToToggle.SetActive(true); // Show UI
        isPlacementUIHidden = false;
        if (mainCameraController != null) mainCameraController.SetActive(false); // Disable camera movement
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
                rend.materials = transparentMats; // Use .materials for instancing
            }
        }
        else
        { // Restore original materials
            int materialIndex = 0;
            foreach (Renderer rend in renderers)
            {
                int materialCount = rend.sharedMaterials.Length; // Use sharedMaterials to get count
                if (originalMaterials.Count >= materialIndex + materialCount)
                {
                    Material[] originalMats = new Material[materialCount];
                    for (int i = 0; i < materialCount; i++)
                    {
                        // Check if original material exists before assigning
                        if (materialIndex + i < originalMaterials.Count)
                            originalMats[i] = originalMaterials[materialIndex + i];
                        else
                        {
                            Debug.LogWarning($"Missing original material at index {materialIndex + i} for renderer {rend.name}");
                            // Optionally assign a default material here
                        }
                    }
                    rend.materials = originalMats; // Restore instances using .materials
                    materialIndex += materialCount;
                }
                else { Debug.LogWarning($"Not enough cached materials ({originalMaterials.Count}) to restore for {rend.name} starting at index {materialIndex} (needs {materialCount})"); }
            }
            originalMaterials.Clear(); // Clear after attempting restoration
        }
    }
}

// Helper utility for LayerMask conversion (Place outside the main class or in a separate file)
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