using UnityEngine;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;

public class VirtualWallCreator : MonoBehaviour
{
  // 靜態日誌方法，同時輸出到 Unity 控制台和 Immersive Debugger
  private static void Log(string message)
  {
    Debug.Log("[VirtualWallCreator] " + message);
    SendToImmersiveDebugger("[VirtualWallCreator] " + message, LogType.Log);
  }

  private static void LogWarning(string message)
  {
    Debug.LogWarning("[VirtualWallCreator] " + message);
    SendToImmersiveDebugger("[VirtualWallCreator] " + message, LogType.Warning);
  }

  private static void LogError(string message)
  {
    Debug.LogError("[VirtualWallCreator] " + message);
    SendToImmersiveDebugger("[VirtualWallCreator] " + message, LogType.Error);
  }

  private static void SendToImmersiveDebugger(string message, LogType type = LogType.Log)
  {
    try
    {
      // 使用反射來動態查找 ImmersiveDebuggerManager
      System.Type debuggerType = System.Type.GetType("ImmersiveDebuggerManager");
      if (debuggerType != null)
      {
        var debugger = Object.FindObjectOfType(debuggerType);
        if (debugger != null)
        {
          var logMethod = debuggerType.GetMethod("LogMessage", new System.Type[] { typeof(string), typeof(LogType) });
          if (logMethod != null)
          {
            logMethod.Invoke(debugger, new object[] { message, type });
          }
          else
          {
            // 嘗試沒有 LogType 參數的方法
            logMethod = debuggerType.GetMethod("LogMessage", new System.Type[] { typeof(string) });
            if (logMethod != null)
            {
              logMethod.Invoke(debugger, new object[] { message });
            }
          }
        }
      }
    }
    catch
    {
      // Immersive Debugger 不可用，靜默忽略
    }
  }
  [Header("Wall Configuration")]
  public Material wallMaterial;
  public float wallHeight = 3.0f;
  public float wallThickness = 0.1f;
  public float raycastDistance = 10f;

  [Header("Controller References")]
  public OVRControllerHelper leftController;
  public OVRControllerHelper rightController;
  // Optional explicit anchors (assign in inspector). If null will fallback to OVRControllerHelper or GameObject.Find at Start().
  public Transform leftControllerAnchor;
  public Transform rightControllerAnchor;

  // cached transforms used for ray origins/directions
  private Transform leftAnchorTransform;
  private Transform rightAnchorTransform;
  public enum RaySource { Anchor, ControllerHelper }
  [Header("Ray Source")]
  public RaySource raySource = RaySource.Anchor;

  [Header("Raycast Settings")]
  public LayerMask groundLayer = 1; // 地面圖層
  [Header("Wall Layer")]
  [Tooltip("Layer mask used when raycasting against created walls for cutout mode")]
  public LayerMask wallLayer = ~0; // Default to all layers
  [Tooltip("Name of the layer to assign to created walls")]
  public string wallLayerName = "Wall";
  public GameObject raycastIndicator; // 射線指示器
  [Header("Cutout Defaults")]
  [Tooltip("Default transparent cutout prefab to assign to newly created walls' WallCutout components.")]
  public GameObject defaultTransparentCutoutPrefab;
  [Header("MRUK Settings")]
  [Tooltip("If enabled, wall placement will be restricted to MRUK-scanned floor areas when available.")]
  public bool restrictToScannedFloor = false;

  private List<Vector3> wallPoints = new List<Vector3>();
  private List<Vector3> cutoutPoints = new List<Vector3>();
  private GameObject currentWall;
  // The WallCutout currently collecting polygon points (if any)
  private WallCutout currentPolygonWall = null;
  private bool isCreatingWall = false;
  private LineRenderer previewLine;
  private LineRenderer rayLine;
  private GameObject rayIndicator;
  // Tool mode: Create walls or Cutout existing walls
  public enum ToolMode { Create, Cutout }
  [Header("Tool Mode")]
  public ToolMode toolMode = ToolMode.Create;
  [Header("MRUK Runtime")]
  [Tooltip("If enabled, attempt to use MRUK raycasts (floor anchors) before Physics.Raycast.")]
  public bool useMRUKRaycast = true;
  public bool requestMRUKSceneOnStart = false;
  private bool mrukReady = false;
  private MRUKRoom cachedRoom = null;
  private MRUKAnchor cachedFloor = null;
  public float mrukInitTimeout = 5f;

  void Start()
  {
    Debug.Log("VirtualWallCreator Start() called");

    // Setup preview line renderer
    previewLine = gameObject.AddComponent<LineRenderer>();
    previewLine.material = CreateLineMaterial();
    previewLine.startColor = Color.yellow;
    previewLine.endColor = Color.yellow;
    previewLine.startWidth = 0.02f;
    previewLine.endWidth = 0.02f;
    previewLine.useWorldSpace = true;
    previewLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    previewLine.receiveShadows = false;
    previewLine.positionCount = 0;

    // Setup ray visualization line (separate from preview)
    GameObject rayLineObj = new GameObject("ControllerRayLine");
    rayLineObj.transform.SetParent(transform, false);
    rayLine = rayLineObj.AddComponent<LineRenderer>();
    rayLine.material = CreateLineMaterial();
    rayLine.startColor = Color.cyan;
    rayLine.endColor = Color.cyan;
    rayLine.startWidth = 0.01f;
    rayLine.endWidth = 0.01f;
    rayLine.useWorldSpace = true;
    rayLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    rayLine.receiveShadows = false;
    rayLine.positionCount = 2;
    rayLine.enabled = false;

    // Create default wall material if not assigned
    if (wallMaterial == null)
    {
      wallMaterial = CreateDefaultWallMaterial();
    }

    // Create raycast indicator
    CreateRaycastIndicator();

    // Try to subscribe to MRUK events and refresh room data
    TryInitializeMRUK();

    // Cache controller anchor transforms with fallbacks
    if (leftControllerAnchor != null)
      leftAnchorTransform = leftControllerAnchor;
    else if (leftController != null)
      leftAnchorTransform = leftController.transform;
    else
      leftAnchorTransform = GameObject.Find("LeftControllerAnchor")?.transform;

    if (rightControllerAnchor != null)
      rightAnchorTransform = rightControllerAnchor;
    else if (rightController != null)
      rightAnchorTransform = rightController.transform;
    else
      rightAnchorTransform = GameObject.Find("RightControllerAnchor")?.transform;

    if (leftAnchorTransform == null || rightAnchorTransform == null)
    {
      Debug.LogWarning("VirtualWallCreator: one or both controller anchors are not assigned or found. Please assign LeftControllerAnchor and RightControllerAnchor in the inspector.");
    }

    // Update wallLayer mask to match the layer name
    int layerIndex = LayerMask.NameToLayer(wallLayerName);
    if (layerIndex != -1)
    {
      wallLayer = 1 << layerIndex;
      Debug.Log("Wall layer set to: " + wallLayerName + " (index: " + layerIndex + ", mask: " + wallLayer.value + ")");
    }
    else
    {
      wallLayer = 1; // Fallback to layer 1
      Debug.LogWarning("Wall layer '" + wallLayerName + "' not found, using layer 1. Available layers:");
      for (int i = 0; i < 32; i++)
      {
        string layerName = LayerMask.LayerToName(i);
        if (!string.IsNullOrEmpty(layerName))
        {
          Debug.Log("  Layer " + i + ": " + layerName);
        }
      }
    }

    Debug.Log("VirtualWallCreator initialized. Tool mode: " + toolMode + ", Wall layer: " + wallLayer.value + " (" + wallLayerName + ")");
  }

  void TryInitializeMRUK()
  {
    // If MRUK already exists and is initialized, grab current room
    if (MRUK.Instance != null)
    {
      MRUK.Instance.RegisterSceneLoadedCallback(OnMRUKSceneLoaded);
      MRUK.Instance.RoomUpdatedEvent.AddListener(OnMRUKRoomUpdated);
      // Try immediate refresh
      TryRefreshRoom();
    }
    else
    {
      // Try to find MRUK in scene
      var mr = FindObjectOfType<MRUK>();
      if (mr != null)
      {
        mr.RegisterSceneLoadedCallback(OnMRUKSceneLoaded);
        mr.RoomUpdatedEvent.AddListener(OnMRUKRoomUpdated);
        TryRefreshRoom();
      }
      else
      {
        if (requestMRUKSceneOnStart)
        {
          Debug.LogWarning("MRUK instance not found in scene. Ensure MRUK is present to use scanned room data.");
        }
      }
    }
  }

  void OnMRUKSceneLoaded()
  {
    mrukReady = true;
    TryRefreshRoom();
    Debug.Log("MRUK scene loaded - VirtualWallCreator aware");
  }

  void OnMRUKRoomUpdated(MRUKRoom room)
  {
    // update cached room if it matches current
    TryRefreshRoom();
  }

  void TryRefreshRoom()
  {
    try
    {
      if (MRUK.Instance == null) return;
      var room = MRUK.Instance.GetCurrentRoom();
      cachedRoom = room;
      if (cachedRoom != null)
      {
        cachedFloor = cachedRoom.FloorAnchor;
        mrukReady = true;
        Debug.Log("VirtualWallCreator: cached MRUK room and floor anchor refreshed. FloorAnchor=" + (cachedFloor != null ? cachedFloor.name : "null"));
      }
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("TryRefreshRoom failed: " + ex.Message);
    }
  }

  bool MRUKRaycast(Vector3 origin, Vector3 direction, out RaycastHit hitInfo)
  {
    hitInfo = new RaycastHit();
    if (!mrukReady || cachedFloor == null) return false;
    try
    {
      Ray ray = new Ray(origin, direction);
      // Try floor anchor first
      if (cachedFloor.Raycast(ray, raycastDistance, out hitInfo, MRUKAnchor.ComponentType.Plane))
      {
        return true;
      }
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("MRUKRaycast failed: " + ex.Message);
    }
    return false;
  }

  void Update()
  {
    Debug.Log("VirtualWallCreator Update called, toolMode: " + toolMode + ", Time.frameCount: " + Time.frameCount);

    // ensure anchors remain valid; try to recover if missing
    EnsureAnchors();
    HandleRaycastVisualization();
    // Mode toggle (X button) - switch between Create and Cutout modes
    if (OVRInput.GetDown(OVRInput.RawButton.X))
    {
      Debug.Log("X button pressed - toggling tool mode");
      ToggleToolMode();
    }

    if (toolMode == ToolMode.Create)
    {
      HandleWallCreation();
      UpdatePreview();
    }
    else // Cutout mode
    {
      Debug.Log("In cutout mode, checking for button inputs...");
      HandleCutoutMode();
      UpdateCutoutPreview();
    }
  }

  void ToggleToolMode()
  {
    Debug.Log("ToggleToolMode called. Current mode: " + toolMode);
    if (toolMode == ToolMode.Create)
    {
      toolMode = ToolMode.Cutout;
      // Cancel any in-progress wall creation
      if (isCreatingWall) CancelWallCreation();
      // If we have a current wall, set it as the polygon target
      if (currentWall != null)
      {
        currentPolygonWall = currentWall.GetComponent<WallCutout>();
        if (currentPolygonWall != null)
        {
          currentPolygonWall.StartPolygonCutout();
          Debug.Log("Started polygon cutout on current wall: " + currentWall.name);
        }
      }
      Debug.Log("Tool switched to Cutout mode");
    }
    else
    {
      toolMode = ToolMode.Create;
      // Cancel any active polygon on the current polygon wall
      if (currentPolygonWall != null)
      {
        currentPolygonWall.CancelPolygonCutout();
        currentPolygonWall = null;
      }
      Debug.Log("Tool switched to Create mode");
    }
  }

  // Handle input and routing for cutout mode. Uses A button to add polygon points, B button to finish polygon, and Y button to cancel.
  void HandleCutoutMode()
  {
    Log("HandleCutoutMode: Checking for A button press...");

    // 檢查所有按鈕的狀態以進行調試
    Log("Button states - A: " + OVRInput.Get(OVRInput.RawButton.A) +
         ", B: " + OVRInput.Get(OVRInput.RawButton.B) +
         ", X: " + OVRInput.Get(OVRInput.RawButton.X) +
         ", Y: " + OVRInput.Get(OVRInput.RawButton.Y));

    // A button to select two points for rectangular cutout
    if (OVRInput.GetDown(OVRInput.RawButton.A))
    {
      Log("A button pressed in cutout mode - selecting cutout point");
      Transform src = GetRightHandTransform();
      if (src == null)
      {
        LogError("Right controller transform not found!");
        return;
      }

      Vector3 rayOrigin = src.position;
      Vector3 rayDirection = src.forward;

      RaycastHit hit;
      if (Physics.Raycast(rayOrigin, rayDirection, out hit, raycastDistance, wallLayer))
      {
        Log("Raycast hit: " + hit.collider.gameObject.name + " at position " + hit.point);

        // If there's a WallCutout component on the hit wall
        var hitWc = hit.collider.gameObject.GetComponent<WallCutout>();
        if (hitWc != null)
        {
          // If there is already an active polygon on this same wall, route the point to it
          if (currentPolygonWall != null && currentPolygonWall.gameObject == hit.collider.gameObject && currentPolygonWall.IsCreatingPolygon())
          {
            Log("Routing polygon point to currentPolygonWall for " + hit.collider.gameObject.name);
            currentPolygonWall.AddPolygonPoint(hit.point);
          }
          else if (currentPolygonWall == null || !currentPolygonWall.IsCreatingPolygon())
          {
            // Start polygon mode on the hit wall and add the first point
            currentPolygonWall = hitWc;
            currentPolygonWall.StartPolygonCutout();
            Log("Started polygon cutout on " + hit.collider.gameObject.name + " and adding first point");
            currentPolygonWall.AddPolygonPoint(hit.point);
          }
          else
          {
            // Fallback to rectangle flow if polygon state is ambiguous
            if (cutoutPoints.Count < 2)
            {
              cutoutPoints.Add(hit.point);
              Log("Added cutout point " + cutoutPoints.Count + " at " + hit.point);
              if (cutoutPoints.Count == 2)
              {
                // Prefer direct WallCutout component API if present
                var wc = hit.collider.gameObject.GetComponent<WallCutout>();
                if (wc != null)
                {
                  Log("Creating rect cutout via WallCutout.ApplyRectCutout on " + hit.collider.gameObject.name);
                  wc.ApplyRectCutout(cutoutPoints[0], cutoutPoints[1]);
                }
                else
                {
                  LogWarning("CreateWallCutout: WallCutout component missing on " + hit.collider.gameObject.name);
                  CreateWallCutout(hit.collider.gameObject, cutoutPoints[0], cutoutPoints[1]);
                }
                cutoutPoints.Clear();
              }
            }
          }
        }
        else
        {
          // No WallCutout on hit object -> rectangle flow
          if (cutoutPoints.Count < 2)
          {
            cutoutPoints.Add(hit.point);
            Log("Added cutout point " + cutoutPoints.Count + " at " + hit.point);

            // If we have 2 points, create the cutout
            if (cutoutPoints.Count == 2)
            {
              LogWarning("CreateWallCutout: WallCutout component missing on " + hit.collider.gameObject.name);
              CreateWallCutout(hit.collider.gameObject, cutoutPoints[0], cutoutPoints[1]);
              cutoutPoints.Clear();
            }
          }
        }
      }
      else
      {
        LogWarning("Raycast in cutout mode missed - no wall hit");
      }
    }

    // B button to finish an active polygon cutout (if any)
    if (OVRInput.GetDown(OVRInput.RawButton.B))
    {
      Log("B button pressed - attempting to finish polygon");
      if (currentPolygonWall != null && currentPolygonWall.IsCreatingPolygon())
      {
        currentPolygonWall.FinishPolygonCutoutAndSnap();
        Log("Finished polygon on currentPolygonWall");
        // Reset polygon state after finishing
        currentPolygonWall = null;
      }
      else
      {
        Log("No active polygon to finish");
      }
    }

    // Y button to cancel/clear current cutout selection
    if (OVRInput.GetDown(OVRInput.RawButton.Y))
    {
      Log("Y button pressed - cancelling cutout selection");
      // If we have a polygon wall active on currentPolygonWall, cancel it
      if (currentPolygonWall != null && currentPolygonWall.IsCreatingPolygon())
      {
        currentPolygonWall.CancelPolygonCutout();
        Log("Cancelled polygon on currentPolygonWall");
      }
      else
      {
        // No polygon active on current wall; clear cutout points
        cutoutPoints.Clear();
        Log("Cleared cutout points");
      }
    }
  }

  void EnsureAnchors()
  {
    // If anchors were not found at Start, try to resolve dynamically
    if (leftAnchorTransform == null)
    {
      if (leftControllerAnchor != null) leftAnchorTransform = leftControllerAnchor;
      else if (leftController != null) leftAnchorTransform = leftController.transform;
      else leftAnchorTransform = GameObject.Find("LeftControllerAnchor")?.transform;
    }

    if (rightAnchorTransform == null)
    {
      if (rightControllerAnchor != null) rightAnchorTransform = rightControllerAnchor;
      else if (rightController != null) rightAnchorTransform = rightController.transform;
      else rightAnchorTransform = GameObject.Find("RightControllerAnchor")?.transform;
    }
  }

  Transform GetRightHandTransform()
  {
    Debug.Log("GetRightHandTransform called");
    if (raySource == RaySource.ControllerHelper && rightController != null)
    {
      Debug.Log("Using rightController transform: " + rightController.transform.position);
      return rightController.transform;
    }

    if (rightControllerAnchor != null)
    {
      Debug.Log("Using rightControllerAnchor: " + rightControllerAnchor.position);
      return rightControllerAnchor;
    }

    if (rightAnchorTransform != null)
    {
      Debug.Log("Using rightAnchorTransform: " + rightAnchorTransform.position);
      return rightAnchorTransform;
    }

    if (rightController != null)
    {
      Debug.Log("Using rightController (fallback): " + rightController.transform.position);
      return rightController.transform;
    }

    // Final fallback: try finding by name
    Transform fallback = GameObject.Find("RightControllerAnchor")?.transform;
    if (fallback != null)
    {
      Debug.Log("Using RightControllerAnchor fallback: " + fallback.position);
      return fallback;
    }

    Debug.LogWarning("No right hand transform found!");
    return null;
  }

  void HandleRaycastVisualization()
  {
    // 從右手控制器發射射線
    Transform src = GetRightHandTransform();
    if (src == null) return;

    Vector3 rayOrigin = src.position;
    Vector3 rayDirection = src.forward;

    RaycastHit hit = new RaycastHit();
    bool hitGround = false;
    if (useMRUKRaycast && MRUK.Instance != null)
    {
      hitGround = MRUKRaycast(rayOrigin, rayDirection, out hit);
    }
    if (!hitGround)
    {
      hitGround = Physics.Raycast(rayOrigin, rayDirection, out hit, raycastDistance, groundLayer);
    }
    bool validPlacement = true;
    if (hitGround && restrictToScannedFloor)
    {
      validPlacement = IsPointOnScannedFloor(hit.point);
    }
    if (hitGround)
    {
      // 顯示射線打到的點
      if (rayIndicator != null)
      {
        rayIndicator.SetActive(true);
        rayIndicator.transform.position = hit.point;
        // color indicator based on validity
        var rend = rayIndicator.GetComponent<Renderer>();
        if (rend != null)
        {
          rend.material.color = validPlacement ? Color.cyan : Color.red;
        }
      }

      // update ray line to hit point
      if (rayLine != null)
      {
        rayLine.enabled = true;
        rayLine.startColor = validPlacement ? Color.cyan : Color.red;
        rayLine.endColor = validPlacement ? Color.cyan : Color.red;
        rayLine.SetPosition(0, rayOrigin);
        rayLine.SetPosition(1, hit.point);
      }

      // 可選：顯示射線
      Debug.DrawRay(rayOrigin, rayDirection * hit.distance, Color.green);
    }
    else
    {
      // 隱藏指示器
      if (rayIndicator != null)
      {
        rayIndicator.SetActive(false);
      }

      // update ray line to max distance
      if (rayLine != null)
      {
        rayLine.enabled = true;
        // when not hitting ground, show default color
        rayLine.startColor = Color.cyan;
        rayLine.endColor = Color.cyan;
        rayLine.SetPosition(0, rayOrigin);
        rayLine.SetPosition(1, rayOrigin + rayDirection * raycastDistance);
      }

      // 顯示射線（沒打到東西）
      Debug.DrawRay(rayOrigin, rayDirection * raycastDistance, Color.red);
    }
  }

  void HandleWallCreation()
  {
    // Right controller trigger to start/add points
    if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
    {
      Transform src = GetRightHandTransform();
      if (src == null)
      {
        Debug.LogWarning("Right controller transform not found - cannot perform raycast");
        return;
      }

      // 使用 raycast 打到的點而非控制器位置
      Vector3 rayOrigin = src.position;
      Vector3 rayDirection = src.forward;

      RaycastHit hit = new RaycastHit();
      bool didHit = false;
      if (useMRUKRaycast && MRUK.Instance != null)
      {
        didHit = MRUKRaycast(rayOrigin, rayDirection, out hit);
      }
      if (!didHit)
      {
        didHit = Physics.Raycast(rayOrigin, rayDirection, out hit, raycastDistance, groundLayer);
      }
      if (didHit)
      {
        Vector3 hitPoint = hit.point;

        // Enforce MRUK-scanned floor only: require MRUK ready and point inside scanned floor
        if (!mrukReady || cachedFloor == null)
        {
          Debug.LogWarning("MRUK data not available - wall placement disabled until MRUK room floor is available.");
        }
        else if (!IsPointOnScannedFloor(hitPoint))
        {
          Debug.LogWarning("Attempted to place a wall point outside of scanned floor area. Point ignored.");
        }
        else
        {
          if (!isCreatingWall)
          {
            // Start creating wall
            isCreatingWall = true;
            wallPoints.Clear();
            wallPoints.Add(hitPoint);
            Debug.Log("Started wall creation at raycast point: " + hitPoint);
          }
          else
          {
            // Add point to wall
            wallPoints.Add(hitPoint);
            Debug.Log("Added wall point at raycast point: " + hitPoint);
          }
        }
      }
      else
      {
        Debug.Log("Raycast didn't hit anything - cannot place wall point");
      }
    }

    // Left controller trigger to finish wall
    if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger) && isCreatingWall)
    {
      if (wallPoints.Count >= 2)
      {
        CreateWallMesh();
        isCreatingWall = false;
        wallPoints.Clear();
        previewLine.positionCount = 0;
      }
    }

    // B button to cancel
    if (OVRInput.GetDown(OVRInput.RawButton.Y) && isCreatingWall)
    {
      CancelWallCreation();
    }
  }

  void UpdateCutoutPreview()
  {
    Log("UpdateCutoutPreview called");
    // Find the active WallCutout component that's currently creating a cutout
    WallCutout activeCutout = FindActiveCutout();
    Log("Active cutout found: " + (activeCutout != null ? activeCutout.gameObject.name : "null"));

    if (activeCutout != null && activeCutout.IsCreatingPolygon())
    {
      Log("Active cutout is creating polygon");
      // Get the first point and current raycast position to show preview
      Vector3 firstPoint = activeCutout.GetFirstPolygonLocalPoint();
      Log("First polygon point: " + firstPoint);
      if (firstPoint != Vector3.zero)
      {
        Vector3 worldFirstPoint = activeCutout.transform.TransformPoint(firstPoint);
        Log("World first point: " + worldFirstPoint);

        // Get current raycast position on the wall
        Transform src = GetRightHandTransform();
        if (src != null)
        {
          Log("Right hand transform found, performing raycast for preview");
          RaycastHit hit;
          if (Physics.Raycast(src.position, src.forward, out hit, raycastDistance, wallLayer))
          {
            Log("Preview raycast hit: " + hit.collider.gameObject.name + " at " + hit.point);
            if (hit.collider != null && hit.collider.gameObject == activeCutout.gameObject)
            {
              Log("Preview raycast hit the active cutout wall, updating preview line");
              // Update preview line
              if (previewLine != null)
              {
                previewLine.positionCount = 2;
                previewLine.startColor = Color.red;
                previewLine.endColor = Color.red;
                previewLine.SetPosition(0, worldFirstPoint);
                previewLine.SetPosition(1, hit.point);
                Log("Preview line updated with points: " + worldFirstPoint + " to " + hit.point);
              }
              else
              {
                LogWarning("Preview line is null!");
              }
            }
            else
            {
              Log("Preview raycast hit different object: " + (hit.collider != null ? hit.collider.gameObject.name : "null"));
            }
          }
          else
          {
            Log("Preview raycast missed");
          }
        }
        else
        {
          LogWarning("Right hand transform not found for preview");
        }
      }
      else
      {
        Log("First point is zero, clearing preview");
        // Clear preview if no valid first point
        if (previewLine != null)
        {
          previewLine.positionCount = 0;
        }
      }
    }
    else
    {
      Log("No active cutout or not creating polygon, clearing preview");
      // Clear preview if no active cutout
      if (previewLine != null)
      {
        previewLine.positionCount = 0;
      }
    }
  }

  WallCutout FindActiveCutout()
  {
    // Find all walls and check which one is actively creating a cutout
    WallCutout[] cutouts = FindObjectsOfType<WallCutout>();
    Log("FindActiveCutout: Found " + cutouts.Length + " WallCutout components in scene");

    foreach (WallCutout cutout in cutouts)
    {
      bool isCreating = false;
      // Attempt to invoke IsCreatingPolygon() via reflection for maximum compatibility
      try
      {
        var t = cutout.GetType();
        var m = t.GetMethod("IsCreatingPolygon");
        if (m != null)
        {
          object res = m.Invoke(cutout, null);
          if (res is bool) isCreating = (bool)res;
        }
        else
        {
          // Fallback: consider the component active/enabled as a weak signal
          isCreating = cutout.enabled && cutout.gameObject.activeInHierarchy;
        }
      }
      catch
      {
        // Last-resort fallback
        isCreating = cutout.enabled && cutout.gameObject.activeInHierarchy;
      }

      Log("FindActiveCutout: Checking " + cutout.gameObject.name + " - IsCreatingPolygon/active: " + isCreating);
      if (isCreating)
      {
        Log("FindActiveCutout: Found active cutout on " + cutout.gameObject.name);
        return cutout;
      }
    }

    Log("FindActiveCutout: No active cutout found");
    return null;
  }

  void UpdatePreview()
  {
    if (isCreatingWall && wallPoints.Count > 0)
    {
      List<Vector3> previewPoints = new List<Vector3>(wallPoints);

      // 添加當前 raycast 點到預覽
      Transform src = GetRightHandTransform();
      if (src != null)
      {
        Vector3 rayOrigin = src.position;
        Vector3 rayDirection = src.forward;

        RaycastHit hit = new RaycastHit();
        bool didHit = false;
        if (useMRUKRaycast && MRUK.Instance != null)
        {
          didHit = MRUKRaycast(rayOrigin, rayDirection, out hit);
        }
        if (!didHit)
        {
          didHit = Physics.Raycast(rayOrigin, rayDirection, out hit, raycastDistance, groundLayer);
        }
        if (didHit)
        {
          // only show preview when MRUK floor is available and the point is inside scanned floor
          if (mrukReady && cachedFloor != null && IsPointOnScannedFloor(hit.point))
          {
            previewPoints.Add(hit.point);
          }
        }
      }
      previewLine.positionCount = previewPoints.Count;
      if (previewPoints.Count > 0 && previewLine != null)
      {
        previewLine.SetPositions(previewPoints.ToArray());
      }
    }
  }

  bool IsPointOnScannedFloor(Vector3 worldPoint)
  {
    try
    {
      if (MRUK.Instance == null)
        return false;

      var room = MRUK.Instance.GetCurrentRoom();
      if (room == null)
        return false;

      var floor = room.FloorAnchor;
      if (floor == null)
        return false;

      // Convert world point into floor anchor local 2D position
      Vector3 local = floor.transform.InverseTransformPoint(worldPoint);
      Vector2 local2D = new Vector2(local.x, local.z);
      return floor.IsPositionInBoundary(local2D);
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("IsPointOnScannedFloor check failed: " + ex.Message);
      return false;
    }
  }

  void CreateWallMesh()
  {
    GameObject wallObject = new GameObject("VirtualWall");

    // Calculate wall center and set position
    Vector3 wallCenter = Vector3.zero;
    foreach (Vector3 point in wallPoints)
    {
      wallCenter += point;
    }
    wallCenter /= wallPoints.Count;
    wallObject.transform.position = wallCenter;

    MeshFilter meshFilter = wallObject.AddComponent<MeshFilter>();
    MeshRenderer meshRenderer = wallObject.AddComponent<MeshRenderer>();
    MeshCollider meshCollider = wallObject.AddComponent<MeshCollider>();

    // Add WallCutout component for hole creation
    var wc = wallObject.AddComponent<WallCutout>();
    // Configure defaults so cutout/prefab behavior is integrated
    if (wc != null)
    {
      wc.transparentCutoutPrefab = defaultTransparentCutoutPrefab;
      wc.cutoutParent = wallObject.transform;
      // prefer prefab-based cutouts by default for predictability
      wc.forceUseTransparentCutoutPrefab = true;
      // Set a default cutoutLayer that differs from the wall's layer (wall's layer is set below)
      wc.cutoutLayer = -1;
      // If there's no default prefab assigned at runtime, create a small hidden runtime template cube
      if (wc.transparentCutoutPrefab == null)
      {
        Debug.LogWarning("VirtualWallCreator: defaultTransparentCutoutPrefab is null - creating runtime fallback cutout prefab.");
        GameObject tmpl = GameObject.CreatePrimitive(PrimitiveType.Cube);
        tmpl.name = "__runtime_cutout_template";
        // make it thin in Z so it behaves like a cutout plane
        tmpl.transform.localScale = new Vector3(1f, 1f, 0.02f);
        // remove collider - it's just a visual template
        var col = tmpl.GetComponent<Collider>(); if (col != null) DestroyImmediate(col);
        var rend = tmpl.GetComponent<Renderer>();
        if (rend != null)
        {
          // create a simple transparent material
          Material mat = new Material(Shader.Find("Standard"));
          try { mat.SetFloat("_Mode", 3); } catch { }
          mat.color = new Color(1f, 1f, 1f, 0.15f);
          rend.sharedMaterial = mat;
        }
        // don't show the template in hierarchy during play
        tmpl.hideFlags = HideFlags.HideAndDontSave;
        tmpl.SetActive(false);
        wc.transparentCutoutPrefab = tmpl;
      }
    }

    // Set wall to the correct layer for raycasting
    int targetLayer = LayerMask.NameToLayer(wallLayerName);
    if (targetLayer == -1)
    {
      targetLayer = 0; // Fallback to Default layer
      Debug.LogWarning("Wall layer '" + wallLayerName + "' not found, using Default layer");
    }
    wallObject.layer = targetLayer;
    // Ensure child renderers/colliders use the same layer
    foreach (Transform t in wallObject.transform) { t.gameObject.layer = targetLayer; }
    Debug.Log("Wall created on layer " + targetLayer + " (" + wallLayerName + ")");

    // Generate mesh in local space
    List<Vector3> localPoints = new List<Vector3>();
    foreach (Vector3 point in wallPoints)
    {
      localPoints.Add(point - wallCenter);
    }

    Mesh wallMesh = GenerateWallMesh(localPoints);
    meshFilter.mesh = wallMesh;
    meshRenderer.material = wallMaterial;
    meshCollider.sharedMesh = wallMesh;
    meshCollider.convex = true; // Enable convex for raycast collision detection

    currentWall = wallObject;
    // Track the WallCutout for polygon routing
    currentPolygonWall = currentWall.GetComponent<WallCutout>();
    Debug.Log("Wall created with " + wallPoints.Count + " points. WallCutout component: " + (wallObject.GetComponent<WallCutout>() != null) + ", MeshCollider convex: " + meshCollider.convex + ", position: " + wallCenter);
  }

  Mesh GenerateWallMesh(List<Vector3> points)
  {
    Mesh mesh = new Mesh();
    List<Vector3> vertices = new List<Vector3>();
    List<int> triangles = new List<int>();
    List<Vector2> uvs = new List<Vector2>();

    // Create wall segments between points
    for (int i = 0; i < points.Count - 1; i++)
    {
      Vector3 start = points[i];
      Vector3 end = points[i + 1];
      Vector3 direction = (end - start).normalized;
      Vector3 perpendicular = Vector3.Cross(direction, Vector3.up).normalized * (wallThickness / 2f);

      // Create quad for wall segment
      int vertexStart = vertices.Count;

      // Bottom vertices (確保正確的繞行順序)
      vertices.Add(start - perpendicular); // 0
      vertices.Add(start + perpendicular); // 1
      vertices.Add(end + perpendicular);   // 2
      vertices.Add(end - perpendicular);   // 3

      // Top vertices
      vertices.Add(start - perpendicular + Vector3.up * wallHeight); // 4
      vertices.Add(start + perpendicular + Vector3.up * wallHeight); // 5
      vertices.Add(end + perpendicular + Vector3.up * wallHeight);   // 6
      vertices.Add(end - perpendicular + Vector3.up * wallHeight);   // 7

      // Front face triangles (修正面向)
      triangles.AddRange(new int[] {
                vertexStart + 0, vertexStart + 1, vertexStart + 4,
                vertexStart + 1, vertexStart + 5, vertexStart + 4
            });

      // Back face triangles (修正面向)
      triangles.AddRange(new int[] {
                vertexStart + 2, vertexStart + 3, vertexStart + 6,
                vertexStart + 3, vertexStart + 7, vertexStart + 6
            });

      // Top face
      triangles.AddRange(new int[] {
                vertexStart + 4, vertexStart + 5, vertexStart + 6,
                vertexStart + 4, vertexStart + 6, vertexStart + 7
            });

      // Bottom face
      triangles.AddRange(new int[] {
                vertexStart + 0, vertexStart + 3, vertexStart + 2,
                vertexStart + 0, vertexStart + 2, vertexStart + 1
            });

      // Side faces
      triangles.AddRange(new int[] {
                // Left side
                vertexStart + 0, vertexStart + 4, vertexStart + 7,
                vertexStart + 0, vertexStart + 7, vertexStart + 3,
                // Right side
                vertexStart + 1, vertexStart + 2, vertexStart + 6,
                vertexStart + 1, vertexStart + 6, vertexStart + 5
            });

      // Add UVs (改善 UV 映射)
      float segmentLength = Vector3.Distance(start, end);
      uvs.AddRange(new Vector2[] {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 0), new Vector2(0, 0), // Bottom
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(0, 1)  // Top
            });
    }

    mesh.vertices = vertices.ToArray();
    mesh.triangles = triangles.ToArray();
    mesh.uv = uvs.ToArray();
    mesh.RecalculateNormals();
    mesh.RecalculateBounds();

    return mesh;
  }

  void CreateRaycastIndicator()
  {
    // 創建射線指示器
    rayIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
    rayIndicator.transform.localScale = Vector3.one * 0.05f;
    rayIndicator.name = "RaycastIndicator";

    // 設置材質
    Renderer renderer = rayIndicator.GetComponent<Renderer>();
    Material indicatorMat = new Material(Shader.Find("Standard"));
    indicatorMat.color = Color.cyan;
    indicatorMat.SetFloat("_Metallic", 0.5f);
    indicatorMat.SetFloat("_Smoothness", 0.8f);
    renderer.material = indicatorMat;

    // 移除碰撞器
    Destroy(rayIndicator.GetComponent<Collider>());
    rayIndicator.SetActive(false);
  }

  Material CreateLineMaterial()
  {
    Material lineMat = new Material(Shader.Find("Sprites/Default"));
    lineMat.color = Color.yellow;
    return lineMat;
  }

  Material CreateDefaultWallMaterial()
  {
    Material mat = new Material(Shader.Find("Standard"));
    mat.color = Color.white;
    mat.SetFloat("_Metallic", 0.0f);
    mat.SetFloat("_Smoothness", 0.1f);
    return mat;
  }

  void CancelWallCreation()
  {
    isCreatingWall = false;
    wallPoints.Clear();
    previewLine.positionCount = 0;
    Debug.Log("Wall creation cancelled");
  }

  // Helper: create cutout on wall object if WallCutout component absent (legacy path)
  void CreateWallCutout(GameObject wallObject, Vector3 a, Vector3 b)
  {
    var wc = wallObject.GetComponent<WallCutout>();
    if (wc != null)
    {
      wc.ApplyRectCutout(a, b);
      return;
    }

    // Legacy fallback: attempt to modify mesh directly (kept simple)
    var mf = wallObject.GetComponent<MeshFilter>();
    if (mf == null || mf.mesh == null)
    {
      LogError("CreateWallCutout: No mesh on wall object " + wallObject.name);
      return;
    }

    // Use naive triangle removal centered on the rectangle defined by a/b
    Vector3[] corners = new Vector3[2] { a, b };
    Vector3 localA = wallObject.transform.InverseTransformPoint(a);
    Vector3 localB = wallObject.transform.InverseTransformPoint(b);
    Vector3 min = Vector3.Min(localA, localB);
    Vector3 max = Vector3.Max(localA, localB);
    Bounds hole = new Bounds((min + max) * 0.5f, max - min);

    Mesh original = mf.mesh;
    Vector3[] verts = original.vertices;
    int[] tris = original.triangles;
    List<int> newTris = new List<int>();

    for (int i = 0; i < tris.Length; i += 3)
    {
      Vector3 triCenter = (verts[tris[i]] + verts[tris[i + 1]] + verts[tris[i + 2]]) / 3f;
      if (!hole.Contains(triCenter))
      {
        newTris.Add(tris[i]); newTris.Add(tris[i + 1]); newTris.Add(tris[i + 2]);
      }
    }

    Mesh m2 = new Mesh();
    m2.vertices = verts;
    m2.triangles = newTris.ToArray();
    try { m2.uv = original.uv; } catch { }
    m2.RecalculateNormals(); m2.RecalculateBounds();
    mf.mesh = m2;
    var mc = wallObject.GetComponent<MeshCollider>(); if (mc != null) mc.sharedMesh = m2;
    Log("CreateWallCutout: Applied legacy triangle-removal cutout to " + wallObject.name);
  }

  // Reflection-based helpers to interact with CutoutManager without compile-time dependency
  // Cutout management is handled directly by VirtualWallCreator/currentPolygonWall and WallCutout
}