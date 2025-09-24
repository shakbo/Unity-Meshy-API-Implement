// PlacementManager.cs
// Attach to: RightControllerAnchor (a Transform representing the right-hand controller pose)
// Bind in Inspector: previewMaterial (optional), LayerMask for raycast, and references to RayVisualizer if used.
// Responsibilities: manage preview GameObject, perform raycasting (MRUK preferred; Physics.Raycast fallback), and place final prefab on trigger.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using XRInputDevice = UnityEngine.XR.InputDevice;
// MRUK (Environment Raycasting) integration is guarded by MRUK_PRESENT so the project
// can compile when MRUK is not available. Define MRUK_PRESENT in Player -> Scripting Define Symbols
// to enable MRUK-specific code.
#if MRUK_PRESENT
using Meta.XR; // EnvironmentRaycastManager & EnvironmentRaycastHit
#endif

namespace InstaSpace
{
  public class PlacementManager : MonoBehaviour
  {
    [Header("References")]
    public Material previewMaterial; // optional; if null we'll generate a simple transparent material
    public LayerMask placementMask = ~0; // default: everything
    public Transform rightController; // assign the right controller anchor

    [Tooltip("If using a RayVisualizer, hook it here to receive hit info")]
    public RayVisualizer rayVisualizer;

    [Header("Interaction SDK (optional)")]
    [Tooltip("Optional: XR Ray Interactor (from XR Interaction Toolkit / Meta Interaction SDK). If assigned, we'll prefer its ray/hit info. Stored as Object to avoid compile-time dependency.")]
    public UnityEngine.Object xrRayInteractor;

    [Header("MRUK (optional)")]
    [Tooltip("Optional: EnvironmentRaycastManager (from MRUK). If assigned and MRUK_PRESENT is defined, we'll use it for environment-aware raycasts.")]
#if MRUK_PRESENT
  public EnvironmentRaycastManager environmentRaycastManager;
#else
    public Object environmentRaycastManager; // placeholder in inspector when MRUK not present
#endif

    [Header("Preview Settings")]
    [Tooltip("Scale applied to preview instances")]
    public Vector3 previewScale = Vector3.one;

    [Tooltip("Opacity for the preview material (0..1)")]
    [Range(0f, 1f)]
    public float previewOpacity = 0.6f;

    [Tooltip("Render queue to use for preview materials (higher draws later)")]
    public int previewRenderQueue = 3100;

    [Tooltip("Small offset applied along the hit normal to avoid z-fighting (meters)")]
    public float previewOffset = 0.01f;

    [Header("XR Input")]
    [Tooltip("XR Node used for the right-hand controller if not using Interaction SDK")]
    public XRNode rightHandNode = XRNode.RightHand;

    private InputDevice _rightDevice;
    private bool _rightDeviceInitialized = false;

    // Internal
    private GameObject _currentPreview;
    private GameObject _selectedPrefab; // prefab chosen (confirmed)
    private GameObject _previewPrefabSource; // source for preview when selection changes
    private Material _generatedPreviewMat;

    void Start()
    {
      // Note: xrRayInteractor is stored as a generic Object to avoid compile-time dependency on XR Interaction Toolkit.
      // If you have the Interaction Toolkit installed, you can re-enable direct event subscriptions by casting the object
      // to XRRayInteractor in a platform-specific compilation block.
      if (previewMaterial == null)
      {
        // Create a simple transparent material using the built-in Standard shader
        Shader std = Shader.Find("Standard");
        if (std)
        {
          _generatedPreviewMat = new Material(std);
          _generatedPreviewMat.SetFloat("_Mode", 3); // Transparent mode
          Color c = new Color(1f, 1f, 1f, previewOpacity);
          _generatedPreviewMat.color = c;
          _generatedPreviewMat.EnableKeyword("_ALPHABLEND_ON");
        }
      }
      else
      {
        _generatedPreviewMat = previewMaterial;
        Color cc = _generatedPreviewMat.color;
        cc.a = previewOpacity;
        _generatedPreviewMat.color = cc;
      }
    }

    private void OnDestroy()
    {
      // no-op: we don't subscribe to xrRayInteractor events at compile time to avoid hard dependency
    }

    // Note: XR Interaction Toolkit event handlers were removed to avoid compile-time dependency.
    // When XR Interaction Toolkit is available you can implement event listeners in a MRUK_PRESENT or toolkit-specific block.

    void InitializeInputDevices()
    {
      if (_rightDeviceInitialized) return;
      _rightDevice = InputDevices.GetDeviceAtXRNode(rightHandNode);
      if (_rightDevice.isValid) _rightDeviceInitialized = true;
    }

    void Update()
    {
      UpdateRaycastAndPreview();
      HandlePlacementInput();
    }

    void UpdateRaycastAndPreview()
    {
      // Initialize ray from right controller by default
      Ray ray = new Ray(rightController.position, rightController.forward);

      // Hit detection pipeline:
      // 1) MRUK (if compiled in via MRUK_PRESENT)
      // 2) XRRayInteractor (Interaction SDK)
      // 3) RayVisualizer (simple LineRenderer ray)
      // 4) Physics.Raycast fallback

      RaycastHit hitInfo = new RaycastHit();
      bool hit = false;
      Vector3 hitPosition = Vector3.zero;
      Vector3 hitNormal = Vector3.up;

#if MRUK_PRESENT
      // Use the MRUK EnvironmentRaycastManager if available
      if (environmentRaycastManager == null)
      {
        // try to find one in the scene
        environmentRaycastManager = FindObjectOfType<EnvironmentRaycastManager>();
      }

      if (environmentRaycastManager != null)
      {
        EnvironmentRaycastHit envHit;
        // Raycast against the environment depth
        if (environmentRaycastManager.Raycast(ray, out envHit))
        {
          hit = true;
          hitPosition = envHit.point;
          hitNormal = envHit.normal;
        }
        else
        {
          // Optionally use PlaceBox logic for box-fit placement. Example usage is left to the consumer.
        }
      }
#endif

      // If MRUK didn't provide a hit, try XRRayInteractor (via reflection to avoid hard dependency)
      if (!hit && xrRayInteractor != null)
      {
        try
        {
          var interactorObj = xrRayInteractor;
          var method = interactorObj.GetType().GetMethod("TryGetCurrent3DRaycastHit");
          if (method != null)
          {
            object[] args = new object[] { new RaycastHit() };
            bool res = (bool)method.Invoke(interactorObj, args);
            if (res && args[0] is RaycastHit rh)
            {
              hit = true;
              hitInfo = rh;
              hitPosition = rh.point;
              hitNormal = rh.normal;
            }
          }
        }
        catch { /* ignore reflection failures */ }
      }

      // If not yet hit, try RayVisualizer's ray or rightController forward
      if (!hit)
      {
        if (rayVisualizer != null)
        {
          ray = rayVisualizer.GetRay();
        }
        else
        {
          if (rightController == null)
          {
            ray = new Ray(transform.position, transform.forward);
          }
          else
          {
            ray = new Ray(rightController.position, rightController.forward);
          }
        }

        if (Physics.Raycast(ray, out hitInfo, 50f, placementMask))
        {
          hit = true;
          hitPosition = hitInfo.point;
          hitNormal = hitInfo.normal;
        }
      }

      if (hit)
      {
        if (_currentPreview != null)
        {
          // Use hitPosition (which is set by all hit detection methods) instead of hitInfo.point
          // Apply a tiny offset along the hit normal to reduce coplanar z-fighting with environment geometry
          _currentPreview.transform.position = hitPosition + hitNormal * previewOffset;
          // Orient to surface normal
          _currentPreview.transform.rotation = Quaternion.FromToRotation(Vector3.up, hitNormal);

          // Make sure preview is visible
          _currentPreview.SetActive(true);
        }

        // Update RayVisualizer with hit info
        if (rayVisualizer != null)
        {
          rayVisualizer.SetHitPoint(hitPosition);
        }
      }
      else
      {
        // If no hit, optionally hide preview or put it in front of controller
        if (_currentPreview != null && rightController != null)
        {
          _currentPreview.transform.position = rightController.position + rightController.forward * 1.5f;
          _currentPreview.transform.rotation = rightController.rotation;

          // Make sure preview is visible even when no hit
          _currentPreview.SetActive(true);
        }

        // Clear RayVisualizer hit
        if (rayVisualizer != null)
        {
          rayVisualizer.ClearHit();
        }
      }
    }

    void HandlePlacementInput()
    {
      // For VR, use OVRInput for right controller trigger
      try
      {
        // Right controller trigger for placement
        if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
        {
          Debug.Log("[PlacementManager] OVRInput right trigger pressed");
          PlaceFromPreview();
        }
      }
      catch (System.Exception e)
      {
        Debug.LogWarning($"[PlacementManager] OVRInput not available: {e.Message}");

        // Fallback to XR InputDevices
        InitializeInputDevices();
        if (_rightDeviceInitialized)
        {
          bool triggerPressed = false;
          if (_rightDevice.TryGetFeatureValue(CommonUsages.triggerButton, out triggerPressed) && triggerPressed)
          {
            PlaceFromPreview();
          }

          // Alternatively, you might check trigger axis value for threshold:
          float triggerValue = 0f;
          if (_rightDevice.TryGetFeatureValue(CommonUsages.trigger, out triggerValue) && triggerValue > 0.8f)
          {
            PlaceFromPreview();
          }
        }

        // If using Meta Interaction SDK, hook into the Ray Interactor/select events instead of polling InputDevices.
      }
    }

    public void UpdatePreview(GameObject prefab)
    {
      _previewPrefabSource = prefab;
      RecreatePreview();
    }

    public void SetSelectedPrefab(GameObject prefab)
    {
      _selectedPrefab = prefab;
      // Optionally keep preview matched to selected
      UpdatePreview(prefab);
    }

    void RecreatePreview()
    {
      if (_currentPreview != null)
      {
        Destroy(_currentPreview);
        _currentPreview = null;
      }

      if (_previewPrefabSource == null)
      {
        Debug.LogWarning("[PlacementManager] No preview prefab source assigned");
        return;
      }

      // Instantiate a copy for previewing (disable colliders and apply preview material)
      _currentPreview = Instantiate(_previewPrefabSource);
      _currentPreview.name = _previewPrefabSource.name + "_Preview";
      _currentPreview.transform.localScale = previewScale;

      Debug.Log($"[PlacementManager] Created preview object: {_currentPreview.name}");

      // Disable colliders and make transparent
      var cols = _currentPreview.GetComponentsInChildren<Collider>(true);
      foreach (var c in cols) c.enabled = false;

      var renderers = _currentPreview.GetComponentsInChildren<Renderer>(true);
      Debug.Log($"[PlacementManager] Found {renderers.Length} renderers in preview object");

      foreach (var r in renderers)
      {
        // Create/assign instance material
        if (_generatedPreviewMat != null)
        {
          // Make an instance so we don't modify shared assets
          var inst = new Material(_generatedPreviewMat);
          // Ensure render queue and ZWrite are set for transparent preview
          try
          {
            inst.renderQueue = previewRenderQueue;
            // Try to disable ZWrite if shader supports it
            if (inst.HasProperty("_ZWrite")) inst.SetFloat("_ZWrite", 0);
          }
          catch { }
          r.material = inst;
          Debug.Log($"[PlacementManager] Applied instanced preview material to renderer: {r.name} (shader={inst.shader.name}, rq={inst.renderQueue})");
        }
        else
        {
          // If no generated preview material, clone existing materials and apply alpha + renderQueue
          var mats = r.materials;
          for (int i = 0; i < mats.Length; i++)
          {
            var m = new Material(mats[i]);
            Color originalColor = m.color;
            m.color = new Color(originalColor.r, originalColor.g, originalColor.b, previewOpacity);
            try
            {
              m.renderQueue = previewRenderQueue;
              if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0);
            }
            catch { }
            mats[i] = m;
          }
          r.materials = mats;
          Debug.Log($"[PlacementManager] Applied cloned/translucent materials on: {r.name}");
        }
      }

      // Make sure preview starts active
      _currentPreview.SetActive(true);
    }

    void PlaceFromPreview()
    {
      if (_currentPreview == null || _previewPrefabSource == null) return;

      // Instantiate final prefab at preview transform
      GameObject go = Instantiate(_previewPrefabSource, _currentPreview.transform.position, _currentPreview.transform.rotation);
      go.transform.localScale = _currentPreview.transform.localScale;

      // Ensure the placed object has colliders and rigidbody if desired
      var cols = go.GetComponentsInChildren<Collider>(true);
      if (cols.Length == 0)
      {
        // Optionally add a default collider (e.g., BoxCollider on root)
        var box = go.AddComponent<BoxCollider>();
        // Approximate size
        box.size = Vector3.one * 0.5f;
      }

      // Keep preview alive for continuous placement or destroy it
      // Here we keep preview but keep it synced to selected prefab
      // If you want to destroy preview after placement, uncomment:
      // Destroy(_currentPreview);
    }

    // -------------------------
    // PlaceBox API examples
    // -------------------------
    /// <summary>
    /// Try to place an axis-aligned box (e.g. a UI panel or furniture bounds) using MRUK PlaceBox if available,
    /// otherwise fall back to a Physics-based placement and overlap check.
    /// Returns the instantiated GameObject on success (or null on failure).
    /// </summary>
    public GameObject TryPlaceBoxAtCurrentRay(Vector3 boxSize, Vector3 up, GameObject prefabToPlace = null)
    {
      // Construct the current ray consistent with UpdateRaycastAndPreview()
      Ray ray;
      if (xrRayInteractor != null)
      {
        // xrRayInteractor is stored as a generic UnityEngine.Object to avoid compile-time dependency.
        // Try to cast to Component to access its transform safely (works for GameObject/Component references).
        var comp = xrRayInteractor as Component;
        if (comp != null)
        {
          ray = new Ray(comp.transform.position, comp.transform.forward);
        }
        else
        {
          // As a last-resort, attempt to find a Transform on the object via reflection (rare) or fallback
          Transform t = null;
          try
          {
            var prop = xrRayInteractor.GetType().GetProperty("transform");
            if (prop != null) t = prop.GetValue(xrRayInteractor) as Transform;
          }
          catch { t = null; }

          if (t != null)
            ray = new Ray(t.position, t.forward);
          else
            ray = new Ray(transform.position, transform.forward);
        }
      }
      else if (rayVisualizer != null)
      {
        ray = rayVisualizer.GetRay();
      }
      else if (rightController != null)
      {
        ray = new Ray(rightController.position, rightController.forward);
      }
      else
      {
        ray = new Ray(transform.position, transform.forward);
      }

      // Prefer MRUK PlaceBox if available
#if MRUK_PRESENT
      if (environmentRaycastManager == null)
      {
        environmentRaycastManager = FindObjectOfType<EnvironmentRaycastManager>();
      }

      if (environmentRaycastManager != null)
      {
        EnvironmentRaycastHit envHit;
        // PlaceBox aligns the box and verifies fit; returns true if placement possible
        if (environmentRaycastManager.PlaceBox(ray, boxSize, up, out envHit))
        {
          GameObject toPlace = prefabToPlace ?? _previewPrefabSource;
          if (toPlace != null)
          {
            var go = Instantiate(toPlace);
            go.transform.SetPositionAndRotation(envHit.point, Quaternion.LookRotation(envHit.normal, up));
            go.transform.localScale = Vector3.one; // keep prefab scale (or adjust as needed)
            return go;
          }
        }
        else
        {
          Debug.Log("PlaceBox: no suitable flat area found (MRUK PlaceBox returned false).");
          return null;
        }
      }
      else
      {
        // Fall through to physics fallback below
      }
#endif

      // Physics fallback: raycast, then check overlap box at the target position
      RaycastHit hitInfo;
      if (Physics.Raycast(ray, out hitInfo, 50f, placementMask))
      {
        Vector3 spawnPos = hitInfo.point;
        Quaternion orientation = Quaternion.identity; // keep axis-aligned

        // Use half extents for OverlapBox
        Vector3 halfExtents = boxSize * 0.5f;
        Collider[] overlaps = Physics.OverlapBox(spawnPos, halfExtents, orientation, placementMask);
        if (overlaps != null && overlaps.Length > 0)
        {
          Debug.Log("PlaceBox fallback: overlaps environment geometry, aborting placement.");
          return null;
        }

        GameObject toPlace = prefabToPlace ?? _previewPrefabSource;
        if (toPlace != null)
        {
          var go = Instantiate(toPlace, spawnPos, Quaternion.LookRotation(hitInfo.normal, up));
          go.transform.localScale = Vector3.one;
          return go;
        }
      }

      return null;
    }

    /// <summary>
    /// Utility: checks if a box at center with halfExtents would overlap environment (uses MRUK CheckBox when available or Physics.OverlapBox fallback).
    /// </summary>
    public bool CheckBoxOverlap(Vector3 center, Vector3 halfExtents, Quaternion orientation)
    {
#if MRUK_PRESENT
      if (environmentRaycastManager == null)
      {
        environmentRaycastManager = FindObjectOfType<EnvironmentRaycastManager>();
      }
      if (environmentRaycastManager != null)
      {
        // MRUK's CheckBox likely expects center, halfExtents, orientation
        return environmentRaycastManager.CheckBox(center, halfExtents, orientation);
      }
#endif
      Collider[] overlaps = Physics.OverlapBox(center, halfExtents, orientation, placementMask);
      return overlaps != null && overlaps.Length > 0;
    }
  }
}
