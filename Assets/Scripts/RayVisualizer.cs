// RayVisualizer.cs
// Attach to: RightControllerAnchor (or the XR Ray Interactor GameObject)
// Responsibilities: draw a line representing the controller ray and provide a GetRay() method for placement logic.

using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace InstaSpace
{
  [RequireComponent(typeof(LineRenderer))]
  public class RayVisualizer : MonoBehaviour
  {
    public float maxDistance = 10f;
    public Color rayColor = Color.cyan;
    public bool show = true;

    [Tooltip("Optional: Pre-made material for LineRenderer. If null, will create one automatically.")]
    public Material lineMaterial;

    private LineRenderer _lr;

    void Awake()
    {
      _lr = GetComponent<LineRenderer>();
      _lr.positionCount = 2;
      _lr.startWidth = 0.01f;
      _lr.endWidth = 0.002f;

      // Debug: Check if lineMaterial is assigned
      if (lineMaterial != null)
      {
        Debug.Log($"[RayVisualizer] Using assigned lineMaterial: {lineMaterial.name}, Shader: {lineMaterial.shader.name}");
        _lr.material = lineMaterial;
      }
      else
      {
        Debug.LogWarning("[RayVisualizer] lineMaterial is null, attempting to find or create material");

        // Try to load from Assets using AssetDatabase (Editor) or create one
        Material loadedMat = null;

#if UNITY_EDITOR
        // In Editor, try to find the material in InstaspaceAuto/Materials
        string[] guids = UnityEditor.AssetDatabase.FindAssets("RayLineMaterial t:Material");
        if (guids.Length > 0)
        {
          string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
          loadedMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
          Debug.Log($"[RayVisualizer] Found RayLineMaterial at path: {path}");
        }
#endif

        if (loadedMat == null)
        {
          loadedMat = CreateLineMaterial();
          Debug.LogWarning("[RayVisualizer] RayLineMaterial not found, created new material. Consider assigning lineMaterial field in Inspector.");
        }
        else
        {
          Debug.Log($"[RayVisualizer] Loaded RayLineMaterial: {loadedMat.name}, Shader: {loadedMat.shader.name}");
        }
        _lr.material = loadedMat;
      }

      // Configure LineRenderer properties
      _lr.startColor = rayColor;
      _lr.endColor = rayColor;
      _lr.useWorldSpace = true;
      _lr.sortingOrder = 1000; // Render on top

      Debug.Log($"[RayVisualizer] Final LineRenderer material: {(_lr.material != null ? _lr.material.name : "NULL")}, Shader: {(_lr.material != null && _lr.material.shader != null ? _lr.material.shader.name : "NULL")}");
    }

    Material CreateLineMaterial()
    {
      // For LineRenderer, use shaders specifically designed for lines
      Shader lineShader = Shader.Find("Sprites/Default") ??
                         Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ??
                         Shader.Find("Universal Render Pipeline/Unlit") ??
                         Shader.Find("Unlit/Color") ??
                         Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply") ??
                         Shader.Find("Hidden/Internal-Colored");

      if (lineShader == null)
      {
        Debug.LogError("[RayVisualizer] No suitable shader found for LineRenderer!");
        return new Material(Shader.Find("Hidden/Internal-Colored"));
      }

      Material mat = new Material(lineShader);
      Debug.Log($"[RayVisualizer] Creating LineRenderer material with shader: {lineShader.name}");

      // Set basic properties for visibility
      if (mat.HasProperty("_Color"))
        mat.SetColor("_Color", rayColor); // Most common property
      else if (mat.HasProperty("_BaseColor"))
        mat.SetColor("_BaseColor", rayColor); // URP
      else if (mat.HasProperty("_TintColor"))
        mat.SetColor("_TintColor", rayColor); // Particles

      // Enable transparency if available
      if (mat.HasProperty("_Mode"))
        mat.SetFloat("_Mode", 2); // Transparent mode for Built-in

      // Set rendering queue for transparency
      mat.renderQueue = 3000;

      return mat;
    }

    private Vector3? _hitPoint = null;

    void Update()
    {
      if (!show)
      {
        _lr.enabled = false;
        return;
      }
      _lr.enabled = true;
      _lr.SetPosition(0, transform.position);

      // If we have a hit point, draw to hit point, otherwise to max distance
      Vector3 endPoint = _hitPoint ?? (transform.position + transform.forward * maxDistance);
      _lr.SetPosition(1, endPoint);
    }

    public Ray GetRay()
    {
      return new Ray(transform.position, transform.forward);
    }

    public void SetHitPoint(Vector3 hitPoint)
    {
      _hitPoint = hitPoint;
    }

    public void ClearHit()
    {
      _hitPoint = null;
    }
  }
}
