using UnityEngine;
using System.Collections.Generic;
#if LIBCSG_DIRECT
using LibCSG;
#endif

// WallCutout: provides rectangular and polygon cutouts for wall meshes.
// Features:
// - Safe runtime reflection call to optional RuntimeCSG.Subtract
// - Keeps wall GameObject transform and bounds center unchanged after mesh replacement
// - Option to prefer single-mesh fallback (triangle removal + welding)
// - Option to use a transparent prefab instead of modifying the mesh
public class WallCutout : MonoBehaviour
{
  [Header("Cutout Settings")]
  public LayerMask wallLayer = ~0;
  public float maxRayDistance = 20f;
  [Tooltip("Multiplier applied to estimated wall thickness to ensure subtractor fully penetrates the wall")]
  public float subtractorDepthMultiplier = 4f;
  [Tooltip("Prefer producing a single modified mesh using triangle-removal (keeps wall as one object) instead of framed-panel rebuild")]
  public bool preferSingleMeshFallback = true;
  [Tooltip("If true, use a transparent prefab to simulate a cutout instead of modifying the wall mesh")]
  public bool useTransparentCutoutPrefab = false;
  [Tooltip("Prefab used when useTransparentCutoutPrefab is enabled. It should be a thin quad or box that visually reveals behind the wall.")]
  public GameObject transparentCutoutPrefab;
  [Tooltip("Parent transform to place instantiated cutout prefabs under (optional). If null, prefabs are parented to this wall.)")]
  public Transform cutoutParent;
  [Tooltip("When true and a transparentCutoutPrefab is assigned, always use prefab-based cutouts instead of modifying the wall mesh.")]
  public bool forceUseTransparentCutoutPrefab = true;
  [Tooltip("If true, prefer using LibCSG runtime library for boolean subtraction when available (fallback to prefab only if not present).")]
  public bool preferLibCsg = true;
  [Tooltip("Layer to place instantiated cutout prefabs on. If -1 (default), a different layer than the wall will be chosen automatically.")]
  public int cutoutLayer = -1;
  [Tooltip("When true, instantiated cutout prefab renderers will have their materials modified to be transparent and optionally render on-top.")]
  public bool forceCutoutMaterialOnTop = true;
  [Tooltip("Alpha value applied to cloned cutout materials (0 = fully transparent, 1 = opaque)")]
  [Range(0f, 1f)]
  public float cutoutAlpha = 0.15f;
  [Header("Debug")]
  [Tooltip("Draw debug markers for triangles/edges that would be removed")]
  public bool debugDrawRemoval = false;
  [Tooltip("How long debug markers live (seconds)")]
  public float debugMarkerLifetime = 5f;

  private LineRenderer previewLine;
  private bool isCreatingPolygon = false;
  private List<Vector3> polygonPoints = new List<Vector3>();
  // Track instantiated transparent cutouts so we can remove them later
  private List<GameObject> spawnedCutouts = new List<GameObject>();
  // Backup original meshes so we can always subtract from the pristine source (prevents cumulative errors)
  private System.Collections.Generic.Dictionary<MeshFilter, Mesh> originalMeshMap = new System.Collections.Generic.Dictionary<MeshFilter, Mesh>();
  // Backup original materials for MeshFilters/Renderers so we can restore shaders/materials after mesh replacement
  private System.Collections.Generic.Dictionary<MeshFilter, Material[]> originalMaterialMap = new System.Collections.Generic.Dictionary<MeshFilter, Material[]>();
  // Active subtractor meshes for this wall (each polygon cutout added)
  private System.Collections.Generic.List<Mesh> activeSubtractorMeshes = new System.Collections.Generic.List<Mesh>();
  // CSG batching state
  [Tooltip("Delay (seconds) to batch multiple cutouts into a single CSG pass")] public float csgBatchDelay = 0.18f;
  private Coroutine csgCoroutine = null;
  private bool csgPending = false;
  [Header("Performance & Simplification")]
  [Tooltip("Enable simple runtime mesh simplification (vertex clustering) when CSG results are too complex")] public bool enableMeshSimplification = true;
  [Tooltip("Cell size used for vertex clustering simplification (world units)")] public float simplificationCellSize = 0.02f;
  [Tooltip("Maximum allowed vertex count for union subtractor before simplification is applied")] public int maxCSGUnionVertices = 20000;
  [Tooltip("Maximum allowed vertex count per mesh for attempting CSG; if exceeded and fallback enabled, the code will avoid CSG and use visual prefabs")] public int maxPerMeshVertexLimit = 15000;
  [Tooltip("If true, when geometry is too complex for CSG the system will keep visual prefab fallbacks instead of attempting CSG")]
  public bool fallbackToPrefabIfTooComplex = true;

  void Start()
  {
    previewLine = gameObject.AddComponent<LineRenderer>();
    previewLine.material = new Material(Shader.Find("Sprites/Default"));
    previewLine.positionCount = 0;
    previewLine.startWidth = previewLine.endWidth = 0.02f;
    previewLine.numCapVertices = 2;
    previewLine.startColor = previewLine.endColor = Color.red;

    // If we prefer LibCSG and it's available, don't force transparent prefab fallback
    try
    {
      if (preferLibCsg && IsLibCsgAvailable())
      {
        forceUseTransparentCutoutPrefab = false;
        useTransparentCutoutPrefab = false;
        Debug.Log("WallCutout: LibCSG detected — preferring runtime CSG subtraction.");
      }
    }
    catch { }
  }

  void Update()
  {
    UpdatePreview();
  }

  void UpdatePreview()
  {
    // Polygon preview is drawn when polygonPoints has content (managed by AddPolygonPoint and VirtualWallCreator)
    if (previewLine == null) return;
    if (polygonPoints != null && polygonPoints.Count > 0)
    {
      int cnt = polygonPoints.Count;
      previewLine.positionCount = Mathf.Max(0, cnt > 0 ? cnt + 1 : 0);
      for (int i = 0; i < cnt; i++) previewLine.SetPosition(i, transform.TransformPoint(polygonPoints[i]));
      if (cnt > 0) previewLine.SetPosition(cnt, transform.TransformPoint(polygonPoints[0]));
    }
    else
    {
      previewLine.positionCount = 0;
    }
  }

  Transform GetRightHandTransform()
  {
    var right = GameObject.Find("RightControllerAnchor");
    if (right != null) return right.transform;
    right = GameObject.Find("RightHandAnchor");
    if (right != null) return right.transform;
    var helper = FindObjectOfType<OVRControllerHelper>();
    if (helper != null) return helper.transform;
    return null;
  }

  // Public API
  public void ApplyRectCutout(Vector3 worldA, Vector3 worldB)
  {
    Vector3 localA = transform.InverseTransformPoint(worldA);
    Vector3 localB = transform.InverseTransformPoint(worldB);
    Vector3 min = Vector3.Min(localA, localB);
    Vector3 max = Vector3.Max(localA, localB);
    Vector3 localCenter = (min + max) * 0.5f;
    Vector3 localSize = max - min;
    float thickness = Mathf.Abs(GetWallThickness());
    if (thickness <= 0f) thickness = 0.1f;
    // Preserve original wall thickness (do not double it)
    localSize.z = Mathf.Max(localSize.z, thickness);
    Vector3 worldCenter = transform.TransformPoint(localCenter);
    Vector3 worldSize = transform.TransformVector(localSize);
    CreateHoleMesh(worldCenter, worldSize);
  }

  public void CancelCutout() { if (previewLine != null) previewLine.positionCount = 0; }
  public void StartPolygonCutout() { isCreatingPolygon = true; polygonPoints.Clear(); if (previewLine != null) previewLine.positionCount = 0; }
  public void AddPolygonPoint(Vector3 worldPoint)
  {
    if (!isCreatingPolygon) return;
    polygonPoints.Add(transform.InverseTransformPoint(worldPoint));
    if (previewLine != null)
    {
      // Show closed polygon preview by duplicating first point at the end
      int cnt = polygonPoints.Count;
      previewLine.positionCount = Mathf.Max(0, cnt > 0 ? cnt + 1 : 0);
      for (int i = 0; i < cnt; i++) previewLine.SetPosition(i, transform.TransformPoint(polygonPoints[i]));
      if (cnt > 0) previewLine.SetPosition(cnt, transform.TransformPoint(polygonPoints[0]));
    }
  }
  public bool IsCreatingPolygon() { return isCreatingPolygon; }
  public void CancelPolygonCutout() { isCreatingPolygon = false; polygonPoints.Clear(); if (previewLine != null) previewLine.positionCount = 0; }
  public Vector3 GetFirstPolygonLocalPoint() { if (polygonPoints != null && polygonPoints.Count > 0) return polygonPoints[0]; return Vector3.zero; }
  public void FinishPolygonCutoutAndSnap()
  {
    if (!isCreatingPolygon || polygonPoints.Count < 3) { CancelPolygonCutout(); return; }
    // Create an extruded polygon mesh as a transparent cutout
    CreateTransparentPolygonCutout(polygonPoints);
    isCreatingPolygon = false;
    polygonPoints.Clear();
    if (previewLine != null) previewLine.positionCount = 0;
  }

  // Create an extruded polygon cutout from local-space polygon points (assumes polygonPoints are local to this transform)
  void CreateTransparentPolygonCutout(List<Vector3> localPoints)
  {
    try
    {
      if (localPoints == null || localPoints.Count < 3) return;
      var mf = GetComponent<MeshFilter>();
      float wallLocalZ = 0f;
      float wallLocalThickness = Mathf.Abs(GetWallThickness());
      if (mf != null && mf.mesh != null) wallLocalZ = mf.mesh.bounds.center.z;
      if (wallLocalThickness <= 0f) wallLocalThickness = 0.02f;
      // Make the subtractor thicker than the estimated wall thickness so it fully penetrates the wall
      float half = wallLocalThickness * 0.5f * Mathf.Max(1f, subtractorDepthMultiplier);

      // Build 2D polygon points for triangulation (project to XY)
      List<Vector2> poly2 = new List<Vector2>(localPoints.Count);
      for (int i = 0; i < localPoints.Count; i++) poly2.Add(new Vector2(localPoints[i].x, localPoints[i].y));

      // Clean polygon: remove duplicates and collinear points, ensure CCW winding
      poly2 = CleanPolygon2D(poly2);

      int[] tris2D = Triangulate(poly2);
      if (tris2D == null || tris2D.Length == 0)
      {
        Debug.LogWarning("WallCutout: Polygon triangulation failed or produced no triangles.");
        return;
      }

      int n = poly2.Count;
      // Prepare vertices: top (z = wallLocalZ + half) then bottom (z = wallLocalZ - half)
      Vector3[] verts = new Vector3[n * 2];
      for (int i = 0; i < n; i++)
      {
        verts[i] = new Vector3(poly2[i].x, poly2[i].y, wallLocalZ + half);
        verts[i + n] = new Vector3(poly2[i].x, poly2[i].y, wallLocalZ - half);
      }

      List<int> tris = new List<int>();
      // Top face (use tris2D order)
      for (int i = 0; i < tris2D.Length; i += 3)
      {
        tris.Add(tris2D[i]); tris.Add(tris2D[i + 1]); tris.Add(tris2D[i + 2]);
      }
      // Bottom face (reverse winding)
      for (int i = 0; i < tris2D.Length; i += 3)
      {
        tris.Add(tris2D[i + 2] + n); tris.Add(tris2D[i + 1] + n); tris.Add(tris2D[i] + n);
      }

      // Side faces
      for (int i = 0; i < n; i++)
      {
        int ni = (i + 1) % n;
        int topA = i; int topB = ni; int botA = i + n; int botB = ni + n;
        // quad: topA, topB, botB, botA
        tris.Add(topA); tris.Add(topB); tris.Add(botB);
        tris.Add(topA); tris.Add(botB); tris.Add(botA);
      }

      Mesh m = new Mesh(); m.vertices = verts; m.triangles = tris.ToArray();
      try { Vector2[] uvs = new Vector2[verts.Length]; for (int i = 0; i < n; i++) uvs[i] = new Vector2(poly2[i].x, poly2[i].y); for (int i = n; i < verts.Length; i++) uvs[i] = uvs[i - n]; m.uv = uvs; } catch { }
      m.RecalculateNormals(); m.RecalculateBounds();

      // Ensure the visual fallback mesh is double-sided so the interior of the hole shows the texture.
      try
      {
        int[] origTris = m.triangles;
        m = DuplicateMeshWithBackfaces(m);
        try { m.RecalculateTangents(); } catch { try { EnsureMeshHasTangents(m); } catch { } }
      }
      catch { }

      // First, try to perform a proper boolean subtraction (CSG) if RuntimeCSG is available.
      try
      {
        var wallMf = GetComponent<MeshFilter>();
        if (wallMf != null && wallMf.mesh != null)
        {
          // Safety guard: avoid calling CSG on extremely large meshes which can hang at runtime
          Mesh csgResult = null;
          int srcV = wallMf.mesh != null ? wallMf.mesh.vertexCount : 0;
          int subV = m != null ? m.vertexCount : 0;
          const int maxVerticesForCsg = 20000; // tunable
          if ((long)srcV * (long)subV > (long)maxVerticesForCsg * 100) // rough heuristic
          {
            Debug.LogWarning($"WallCutout: Skipping CSG because src vertices={srcV} sub vertices={subV} (too large)");
            csgResult = null;
          }
          else
          {
            csgResult = TryRuntimeCsgSubtract(wallMf.mesh, transform, m, transform);
          }
          if (csgResult != null)
          {
            // Replace wall mesh with CSG result and update collider
            // Preserve original materials/shader on this renderer
            try
            {
              var rendMain = wallMf.GetComponent<Renderer>();
              if (rendMain != null && !originalMaterialMap.ContainsKey(wallMf))
              {
                originalMaterialMap[wallMf] = rendMain.sharedMaterials;
              }
            }
            catch { }
            wallMf.mesh = csgResult;
            // Post-process CSG result to ensure normals/tangents/uv2 are correct for lighting
            try { PostProcessCSGResult(csgResult); } catch { }
            try { var mc = wallMf.GetComponent<MeshCollider>(); if (mc != null) { mc.sharedMesh = null; mc.sharedMesh = csgResult; } } catch { }
            // Re-apply original materials to ensure shader is preserved
            try { var rendMain = wallMf.GetComponent<Renderer>(); if (rendMain != null && originalMaterialMap.ContainsKey(wallMf)) rendMain.sharedMaterials = originalMaterialMap[wallMf]; } catch { }
            Debug.Log("WallCutout: Performed CSG subtraction and replaced wall mesh.");
            return;
          }
          else
          {
            Debug.Log("WallCutout: RuntimeCSG subtract not available or returned null - falling back to visual/subdivision methods.");
          }
        }
      }
      catch (System.Exception ex)
      {
        Debug.LogWarning("WallCutout: CSG attempt failed: " + ex.Message);
      }

      // Before creating visual fallback, try to transfer UVs/normals from the wall so textures map correctly on the cutout.
      try { var wallMf2 = GetComponent<MeshFilter>(); if (wallMf2 != null && wallMf2.mesh != null) { try { TransferUVsAndNormalsFromSource(m, wallMf2.mesh); } catch { } } } catch { }

      // Create GameObject under this wall to host the cutout mesh (visual fallback)
      GameObject go = new GameObject("PolygonCutout");
      go.transform.SetParent(cutoutParent != null ? cutoutParent : transform, false);
      var mfOut = go.AddComponent<MeshFilter>(); mfOut.mesh = m;
      var mr = go.AddComponent<MeshRenderer>();

      // Apply transparent material (clone from wall's renderer or a simple default)
      Material mat = null;
      var rend = GetComponent<Renderer>();
      if (rend != null && rend.sharedMaterial != null) mat = new Material(rend.sharedMaterial);
      else mat = new Material(Shader.Find("Standard"));
      try { if (mat.HasProperty("_Color")) { Color c = mat.color; c.a = cutoutAlpha; mat.color = c; } } catch { }
      mr.sharedMaterial = mat;
      // Make the produced mesh double-sided so the interior of the hole renders the material on both sides.
      try { MakeMeshesDoubleSided(go); } catch { }
      // Configure render settings to be transparent
      try { if (mat.HasProperty("_Mode")) { mat.SetFloat("_Mode", 3); mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha); mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); mat.SetInt("_ZWrite", 0); mat.DisableKeyword("_ALPHATEST_ON"); mat.DisableKeyword("_ALPHABLEND_ON"); mat.EnableKeyword("_ALPHAPREMULTIPLY_ON"); mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent; } } catch { }

      // Set layer and material transparency settings
      int wallLayerIndex = gameObject.layer;
      int chosenLayer = cutoutLayer;
      if (chosenLayer < 0)
      {
        if (31 != wallLayerIndex) chosenLayer = 31; else { chosenLayer = 8; for (int l = 8; l <= 30; l++) if (l != wallLayerIndex) { chosenLayer = l; break; } }
      }
      SetLayerRecursively(go, chosenLayer);
      spawnedCutouts.Add(go);
      go.SetActive(true);
      Debug.Log("WallCutout: Created polygon cutout with " + n + " vertices on layer " + chosenLayer);

      // Register this subtractor so we can perform union-based subtraction from original meshes
      activeSubtractorMeshes.Add(m);
      // Schedule a batched CSG pass instead of doing it immediately to avoid heavy repeated work when the user creates multiple cutouts quickly
      try
      {
        ScheduleCSGApply();
      }
      catch (System.Exception ex)
      {
        Debug.LogWarning("WallCutout: Failed to schedule LibCSG subtraction: " + ex.Message);
      }
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: CreateTransparentPolygonCutout failed: " + ex.Message);
    }
  }

  // Remove near-duplicate and collinear points, ensure CCW winding
  List<Vector2> CleanPolygon2D(List<Vector2> pts)
  {
    const float eps = 1e-4f;
    if (pts == null) return new List<Vector2>();
    // Remove consecutive duplicates
    List<Vector2> tmp = new List<Vector2>();
    for (int i = 0; i < pts.Count; i++)
    {
      Vector2 a = pts[i]; Vector2 b = pts[(i + 1) % pts.Count];
      if ((a - b).sqrMagnitude > eps * eps) tmp.Add(a);
    }
    // If last equals first after cleanup, remove last
    if (tmp.Count > 1 && (tmp[0] - tmp[tmp.Count - 1]).sqrMagnitude < eps * eps) tmp.RemoveAt(tmp.Count - 1);

    // Remove collinear points
    List<Vector2> outPts = new List<Vector2>();
    for (int i = 0; i < tmp.Count; i++)
    {
      Vector2 prev = tmp[(i + tmp.Count - 1) % tmp.Count];
      Vector2 curr = tmp[i];
      Vector2 next = tmp[(i + 1) % tmp.Count];
      Vector2 v1 = (curr - prev).normalized; Vector2 v2 = (next - curr).normalized;
      if ((v1 - v2).sqrMagnitude > eps) outPts.Add(curr);
    }

    // Ensure CCW winding (positive area)
    float area = 0f;
    for (int i = 0; i < outPts.Count; i++) { Vector2 a = outPts[i]; Vector2 b = outPts[(i + 1) % outPts.Count]; area += (a.x * b.y - a.y * b.x); }
    if (area < 0f) outPts.Reverse();
    return outPts;
  }

  // Simple ear-clipping triangulation for simple polygons (counter-clockwise)
  int[] Triangulate(List<Vector2> points)
  {
    // Robust triangulation: expects a clean, CCW polygon. We'll operate on the provided list.
    int n = points == null ? 0 : points.Count;
    if (n < 3) return new int[0];

    // Use a small epsilon when comparing areas/distances
    const float eps = 1e-5f;

    // Build index list
    List<int> V = new List<int>(); for (int i = 0; i < n; i++) V.Add(i);
    List<int> result = new List<int>();

    int guard = 0;
    while (V.Count > 2 && guard < 10000)
    {
      bool earFound = false;
      for (int i = 0; i < V.Count; i++)
      {
        int prev = V[(i + V.Count - 1) % V.Count];
        int curr = V[i];
        int next = V[(i + 1) % V.Count];
        Vector2 a = points[prev], b = points[curr], c = points[next];
        // Check if triangle is convex (CCW) using eps tolerance
        float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (cross <= eps) continue; // not a convex ear

        // Check if any other point is inside this triangle (with tolerance)
        bool anyInside = false;
        for (int j = 0; j < points.Count; j++)
        {
          if (j == prev || j == curr || j == next) continue;
          if (PointInTriangle(points[j], a, b, c)) { anyInside = true; break; }
        }
        if (anyInside) continue;

        // Ear found
        result.Add(prev); result.Add(curr); result.Add(next);
        V.RemoveAt(i);
        earFound = true;
        break;
      }
      if (!earFound) break;
      guard++;
    }
    return result.ToArray();
  }

  bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
  {
    // Barycentric technique with small tolerances to include boundary cases
    const float eps = 1e-6f;
    float denom = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
    if (Mathf.Abs(denom) < eps) return false;
    float w1 = ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / denom;
    float w2 = ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / denom;
    float w3 = 1f - w1 - w2;
    return w1 >= -eps && w2 >= -eps && w3 >= -eps;
  }

  void CreateHoleMesh(Vector3 center, Vector3 size)
  {
    // Simplified: always use transparent prefab approach. If prefab is missing, log warning.
    if (transparentCutoutPrefab == null)
    {
      Debug.LogWarning("WallCutout: transparentCutoutPrefab is not assigned and mesh-modification fallback has been removed. Assign a prefab or disable WallCutout on this wall.");
      return;
    }

    // Preserve original wall thickness estimate
    float wallThickness = Mathf.Abs(GetWallThickness()); if (wallThickness <= 0f) wallThickness = Mathf.Abs(size.z);
    Vector3 adjustedSize = size; adjustedSize.z = Mathf.Max(Mathf.Abs(size.z), wallThickness);
    CreateTransparentCutout(center, adjustedSize);
  }

  float GetWallThickness()
  {
    var mf = GetComponent<MeshFilter>(); if (mf != null && mf.mesh != null) return mf.mesh.bounds.size.z; return 0.1f;
  }

  Mesh CreateBoxMesh(float sx, float sy, float sz)
  {
    Mesh m = new Mesh();
    Vector3[] verts = new Vector3[8]
    {
            new Vector3(-sx/2, -sy/2, -sz/2), new Vector3(sx/2, -sy/2, -sz/2), new Vector3(sx/2, sy/2, -sz/2), new Vector3(-sx/2, sy/2, -sz/2),
            new Vector3(-sx/2, -sy/2, sz/2), new Vector3(sx/2, -sy/2, sz/2), new Vector3(sx/2, sy/2, sz/2), new Vector3(-sx/2, sy/2, sz/2)
    };
    int[] tris = new int[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 3, 0, 4, 3, 4, 7 };
    m.vertices = verts; m.triangles = tris; m.RecalculateNormals(); m.RecalculateBounds(); return m;
  }

  Mesh BuildBoxMesh(Vector3 centerLocal, Vector3 sizeLocal)
  {
    Mesh m = CreateBoxMesh(Mathf.Abs(sizeLocal.x), Mathf.Abs(sizeLocal.y), Mathf.Abs(sizeLocal.z));
    Vector3[] v = m.vertices; for (int i = 0; i < v.Length; i++) v[i] += centerLocal; m.vertices = v; m.RecalculateBounds(); m.RecalculateNormals(); return m;
  }

  Mesh MergeMeshes(List<Mesh> meshes)
  {
    if (meshes == null || meshes.Count == 0) return null;
    int vCount = 0; int tCount = 0; foreach (var mm in meshes) { if (mm == null) continue; vCount += mm.vertexCount; tCount += mm.triangles.Length; }
    if (vCount == 0) return null;
    List<Vector3> verts = new List<Vector3>(vCount); List<Vector3> norms = new List<Vector3>(vCount); List<Vector2> uvs = new List<Vector2>(vCount); List<int> tris = new List<int>(tCount);
    int vOffset = 0; foreach (var mm in meshes) { if (mm == null) continue; verts.AddRange(mm.vertices); norms.AddRange(mm.normals.Length == mm.vertexCount ? mm.normals : new Vector3[mm.vertexCount]); uvs.AddRange(mm.uv.Length == mm.vertexCount ? mm.uv : new Vector2[mm.vertexCount]); var t = mm.triangles; for (int i = 0; i < t.Length; i++) tris.Add(t[i] + vOffset); vOffset += mm.vertexCount; }
    Mesh outM = new Mesh(); outM.vertices = verts.ToArray(); try { outM.normals = norms.ToArray(); } catch { }
    try { outM.uv = uvs.ToArray(); } catch { }
    outM.triangles = tris.ToArray(); outM.RecalculateBounds(); outM.RecalculateNormals(); return outM;
  }

  Mesh TryRuntimeCsgSubtract(Mesh srcMesh, Transform srcTransform, Mesh subtractMesh, Transform subtractTransform)
  {
#if LIBCSG_DIRECT
    try
    {
      GameObject ga = srcTransform != null ? srcTransform.gameObject : this.gameObject;
      GameObject tempSubtractor = null;
      try
      {
        // Create a temporary GameObject to host the subtractor mesh so its transform matches subtractTransform
        tempSubtractor = new GameObject("CSG_Subtractor_Temp");
        if (subtractTransform != null)
        {
          tempSubtractor.transform.position = subtractTransform.position;
          tempSubtractor.transform.rotation = subtractTransform.rotation;
          tempSubtractor.transform.localScale = subtractTransform.lossyScale;
        }
        var mfTemp = tempSubtractor.AddComponent<MeshFilter>(); mfTemp.mesh = subtractMesh;
        var mrTemp = tempSubtractor.AddComponent<MeshRenderer>(); mrTemp.enabled = false;

        CSGBrush brushA = new CSGBrush(ga);
        CSGBrush brushB = new CSGBrush(tempSubtractor);
        CSGBrush resultBrush = new CSGBrush("CSG_Result");

        brushA.build_from_mesh(srcMesh);
        brushB.build_from_mesh(subtractMesh);

        CSGBrushOperation op = new CSGBrushOperation();
        op.merge_brushes(Operation.OPERATION_SUBTRACTION, brushA, brushB, ref resultBrush, 0.00001f);

  Mesh outMesh = new Mesh();
        resultBrush.getMesh(outMesh);

        // Ensure UVs exist; if not, try to copy from source mesh as a best-effort
        try
        {
          if (outMesh.uv == null || outMesh.uv.Length == 0)
          {
            if (srcMesh != null && srcMesh.uv != null && srcMesh.uv.Length == srcMesh.vertexCount)
            {
              if (outMesh.vertexCount == srcMesh.vertexCount)
              {
                outMesh.uv = srcMesh.uv;
              }
            }
          }
        }
        catch { }

        // Make backfaces by duplicating vertices+triangles so backface normals/tangents are independent
        try
        {
          outMesh = DuplicateMeshWithBackfaces(outMesh);
          try { outMesh.RecalculateTangents(); } catch { try { EnsureMeshHasTangents(outMesh); } catch { } }
        }
        catch { }

        try { AlignMeshToOriginalBounds(outMesh, srcMesh.bounds); } catch { }
    // Try to transfer UVs/normals from source mesh to keep texture continuity
    try { TransferUVsAndNormalsFromSource(outMesh, srcMesh); } catch { }
  try { CleanMesh(outMesh); } catch { }
  Debug.Log("WallCutout: Performed LibCSG subtraction successfully.");
  return outMesh;
      }
      finally
      {
        try { if (tempSubtractor != null) { try { DestroyImmediate(tempSubtractor); } catch { Destroy(tempSubtractor); } } } catch { }
      }
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: LibCSG subtraction failed: " + ex.Message);
      return null;
    }
#else
    try
    {
      // Reflection fallback: attempt to find LibCSG types at runtime and invoke methods
      System.Type brushType = null;
      System.Type opType = null;
      System.Type operationEnumType = null;
      foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
      {
        if (brushType == null) brushType = asm.GetType("LibCSG.CSGBrush");
        if (opType == null) opType = asm.GetType("LibCSG.CSGBrushOperation");
        if (operationEnumType == null) operationEnumType = asm.GetType("LibCSG.Operation");
        if (brushType != null && opType != null && operationEnumType != null) break;
      }
      if (brushType == null || opType == null || operationEnumType == null)
      {
        Debug.Log("WallCutout: LibCSG types not found in loaded assemblies; skipping CSG subtract.");
        return null;
      }

      GameObject ga = srcTransform != null ? srcTransform.gameObject : this.gameObject;
      GameObject gb = subtractTransform != null ? subtractTransform.gameObject : this.gameObject;

      object brushA = System.Activator.CreateInstance(brushType, new object[] { ga });
      object brushB = System.Activator.CreateInstance(brushType, new object[] { gb });
      object resultBrush = System.Activator.CreateInstance(brushType, new object[] { "CSG_Result" });

      var buildFromMesh = brushType.GetMethod("build_from_mesh", new System.Type[] { typeof(Mesh) });
      if (buildFromMesh == null) { Debug.Log("WallCutout: LibCSG.build_from_mesh not found"); return null; }
      buildFromMesh.Invoke(brushA, new object[] { srcMesh });
      buildFromMesh.Invoke(brushB, new object[] { subtractMesh });

      object op = System.Activator.CreateInstance(opType);
      var mergeMethod = opType.GetMethod("merge_brushes", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
      if (mergeMethod == null) { Debug.Log("WallCutout: LibCSG.merge_brushes not found"); return null; }

      object enumVal = System.Enum.Parse(operationEnumType, "OPERATION_SUBTRACTION");
      object[] invokeParams = new object[] { enumVal, brushA, brushB, resultBrush, 0.00001f };
      mergeMethod.Invoke(op, invokeParams);

      var getMeshMethod = brushType.GetMethod("getMesh", new System.Type[] { typeof(Mesh) });
      if (getMeshMethod == null) { Debug.Log("WallCutout: LibCSG.getMesh not found"); return null; }
      Mesh outMesh = new Mesh();
      getMeshMethod.Invoke(resultBrush, new object[] { outMesh });

      // Ensure UVs exist; if not, generate planar UVs from XY projection (best-effort)
      try
      {
        if (outMesh.uv == null || outMesh.uv.Length == 0)
        {
          Vector3[] verts = outMesh.vertices;
          if (verts != null && verts.Length > 0)
          {
            Vector2[] genUV = new Vector2[verts.Length];
            // project to XY plane using bounds to normalize
            Bounds b = outMesh.bounds;
            float sx = b.size.x != 0f ? b.size.x : 1f;
            float sy = b.size.y != 0f ? b.size.y : 1f;
            for (int i = 0; i < verts.Length; i++)
            {
              genUV[i] = new Vector2((verts[i].x - b.min.x) / sx, (verts[i].y - b.min.y) / sy);
            }
            outMesh.uv = genUV;
          }
        }
      }
      catch { }

      // Make backfaces by duplicating vertices+triangles so backface normals/tangents are independent
      try
      {
        outMesh = DuplicateMeshWithBackfaces(outMesh);
        try { outMesh.RecalculateTangents(); } catch { try { EnsureMeshHasTangents(outMesh); } catch { } }
      }
      catch { }

      try { AlignMeshToOriginalBounds(outMesh, srcMesh.bounds); } catch { }
      try { TransferUVsAndNormalsFromSource(outMesh, srcMesh); } catch { }

      try { CleanMesh(outMesh); } catch { }
      Debug.Log("WallCutout: Performed LibCSG subtraction successfully (via reflection). UVs/backfaces ensured.");
      return outMesh;
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: LibCSG subtraction failed (reflection): " + ex.Message);
      return null;
    }
#endif
  }

  void AlignMeshToOriginalBounds(Mesh newMesh, Bounds originalBounds)
  {
    if (newMesh == null) return;
    try { Vector3[] v = newMesh.vertices; Vector3 offset = newMesh.bounds.center - originalBounds.center; if (offset != Vector3.zero) { for (int i = 0; i < v.Length; i++) v[i] -= offset; newMesh.vertices = v; newMesh.RecalculateBounds(); newMesh.RecalculateNormals(); } }
    catch (System.Exception ex) { Debug.LogWarning("WallCutout: AlignMeshToOriginalBounds failed: " + ex.Message); }
  }

  void CreateTransparentCutout(Vector3 center, Vector3 size)
  {
    try
    {
      if (transparentCutoutPrefab == null) { Debug.LogWarning("WallCutout: transparentCutoutPrefab is not assigned."); return; }
      Transform parent = cutoutParent != null ? cutoutParent : transform;
      var go = Instantiate(transparentCutoutPrefab, center, transform.rotation, parent);
      // Ensure the instantiated cutout is active and visible even if the template prefab was inactive
      try { go.SetActive(true); } catch { }
      // clear any HideFlags copied from a hidden template so it shows in hierarchy if needed
      try { go.hideFlags = HideFlags.None; } catch { }
      // Choose a layer for the cutout that is different from the wall's layer
      int wallLayerIndex = gameObject.layer;
      int chosenLayer = cutoutLayer;
      if (chosenLayer < 0)
      {
        // Try to pick a layer different from the wall; prefer 31, otherwise increment from 8 upwards
        if (31 != wallLayerIndex) chosenLayer = 31;
        else
        {
          chosenLayer = 8;
          for (int l = 8; l <= 30; l++) { if (l != wallLayerIndex) { chosenLayer = l; break; } }
        }
      }
      SetLayerRecursively(go, chosenLayer);
      // Ensure prefab materials are cloned and configured for transparency
      ApplyTransparentMaterials(go, forceCutoutMaterialOnTop, cutoutAlpha);
      // Prefab might be a unit quad; set localScale relative to parent to match requested world size
      // Calculate local scale so world scale equals size (approx). We'll set lossyScale compensation by adjusting localScale.
      Vector3 worldScale = go.transform.lossyScale;
      Vector3 desiredWorldScale = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
      // Avoid division by zero
      Vector3 localScale = new Vector3(
        worldScale.x != 0f ? desiredWorldScale.x / worldScale.x : desiredWorldScale.x,
        worldScale.y != 0f ? desiredWorldScale.y / worldScale.y : desiredWorldScale.y,
        worldScale.z != 0f ? desiredWorldScale.z / worldScale.z : desiredWorldScale.z
      );
      go.transform.localScale = localScale;
      go.transform.position = center;
      spawnedCutouts.Add(go);
      // make sure child renderers are enabled
      try { foreach (var r in go.GetComponentsInChildren<Renderer>(true)) if (r != null) r.enabled = true; } catch { }
      Debug.Log($"WallCutout: Created transparent cutout prefab at {center} size={desiredWorldScale} on layer {chosenLayer}");
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: Failed to instantiate transparent cutout prefab: " + ex.Message);
    }
  }

  void SetLayerRecursively(GameObject root, int layer)
  {
    if (root == null) return;
    try
    {
      root.layer = layer;
      foreach (Transform t in root.transform)
      {
        if (t == null) continue;
        SetLayerRecursively(t.gameObject, layer);
      }
    }
    catch (System.Exception ex) { Debug.LogWarning("WallCutout: SetLayerRecursively failed: " + ex.Message); }
  }

  // Make any meshes under the root double-sided by appending reversed-winding triangles.
  void MakeMeshesDoubleSided(GameObject root)
  {
    if (root == null) return;
    try
    {
      // MeshFilter-based meshes
      var mfs = root.GetComponentsInChildren<MeshFilter>(true);
      foreach (var mf in mfs)
      {
        if (mf == null || mf.sharedMesh == null) continue;
        try { mf.sharedMesh = DuplicateMeshWithBackfaces(mf.sharedMesh); } catch { }
      }

      // Skinned meshes
      var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
      foreach (var sk in skins)
      {
        if (sk == null || sk.sharedMesh == null) continue;
        try { sk.sharedMesh = DuplicateMeshWithBackfaces(sk.sharedMesh); } catch { }
      }
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: MakeMeshesDoubleSided failed: " + ex.Message);
    }
  }

  // Duplicate vertices and triangles to create proper backfaces (avoids shared-vertex normal artifacts)
  Mesh DuplicateMeshWithBackfaces(Mesh src)
  {
    if (src == null) return null;
    try
    {
      var verts = src.vertices; var uvs = src.uv; var normals = src.normals; var tangents = src.tangents; var tris = src.triangles;
      int vCount = verts.Length;
      int tCount = tris.Length;
      Vector3[] nverts = new Vector3[vCount * 2];
      Vector2[] nuvs = uvs != null && uvs.Length == vCount ? new Vector2[vCount * 2] : null;
      Vector3[] nnorms = normals != null && normals.Length == vCount ? new Vector3[vCount * 2] : null;
      Vector4[] ntangs = tangents != null && tangents.Length == vCount ? new Vector4[vCount * 2] : null;

      for (int i = 0; i < vCount; i++)
      {
        nverts[i] = verts[i]; nverts[i + vCount] = verts[i];
        if (nuvs != null) { nuvs[i] = uvs[i]; nuvs[i + vCount] = uvs[i]; }
        if (nnorms != null)
        {
          // Preserve provided normals for front faces and negate for backfaces
          nnorms[i] = normals[i];
          nnorms[i + vCount] = -normals[i];
        }
        if (ntangs != null)
        {
          // Preserve tangents for front faces and negate the xyz part for backfaces
          ntangs[i] = tangents[i];
          ntangs[i + vCount] = new Vector4(-tangents[i].x, -tangents[i].y, -tangents[i].z, tangents[i].w);
        }
      }

      int[] ntris = new int[tCount * 2];
      // copy original
      for (int i = 0; i < tCount; i++) ntris[i] = tris[i];
      // add backfaces using duplicated vertices with reversed winding
      for (int i = 0; i < tCount; i += 3)
      {
        int a = tris[i] + vCount; int b = tris[i + 2] + vCount; int c = tris[i + 1] + vCount;
        int dst = tCount + i;
        ntris[dst] = a; ntris[dst + 1] = b; ntris[dst + 2] = c;
      }

      Mesh outM = new Mesh(); outM.vertices = nverts; outM.triangles = ntris; if (nuvs != null) outM.uv = nuvs; if (nnorms != null) outM.normals = nnorms; if (ntangs != null) outM.tangents = ntangs;
      outM.RecalculateBounds();
      try { outM.RecalculateTangents(); } catch { try { EnsureMeshHasTangents(outM); } catch { } }
      return outM;
    }
    catch { return src; }
  }

  // Try to subtract the provided extruded polygon meshes (union) from each MeshFilter under this GameObject using LibCSG.
  // Returns number of MeshFilters modified.
  int TrySubtractUsingLibCSGOnChildren(List<Vector2> poly2, System.Collections.Generic.List<Mesh> subtractMeshes)
  {
    int modified = 0;
    var mfs = GetComponentsInChildren<MeshFilter>(true);
    // Build a union subtractor mesh from available subtractMeshes
    Mesh unionSubtractor = null;
    try { unionSubtractor = MergeMeshes(subtractMeshes); } catch { }
    if (unionSubtractor == null) return 0;
    // If union is too large, attempt lightweight simplification via vertex clustering to reduce complexity
    try
    {
      if (enableMeshSimplification && unionSubtractor.vertexCount > maxCSGUnionVertices)
      {
        Mesh simp = SimplifyMeshByVertexClustering(unionSubtractor, simplificationCellSize);
        if (simp != null) unionSubtractor = simp;
      }
    }
    catch { }
    // compute world-space union bounds of the subtractor (approx)
    Bounds unionWorldBounds = GetWorldBoundsFromLocal(unionSubtractor.bounds, this.transform);
    foreach (var mf in mfs)
    {
      try
      {
        if (mf == null || mf.mesh == null) continue;
        // quick bounds test: skip meshfilters whose renderer bounds do not intersect the subtractor bounds
        try
        {
          var rend = mf.GetComponent<Renderer>();
          if (rend != null)
          {
            if (!rend.bounds.Intersects(unionWorldBounds)) continue;
          }
        }
        catch { }
        Mesh src = mf.mesh;
        // Per-child safety: skip if src or subtractor are too large
        int srcV = src != null ? src.vertexCount : 0;
        int subV = unionSubtractor != null ? unionSubtractor.vertexCount : 0;
        const int perMeshVertexLimit = 15000; // tunable
        if (srcV > perMeshVertexLimit || subV > perMeshVertexLimit)
        {
          Debug.LogWarning($"WallCutout: Skipping LibCSG for meshfilter {mf.name} because vertex count is large src={srcV} sub={subV}");
          continue;
        }
        // Ensure we always subtract from the original (pristine) mesh to avoid cumulative artifacts
        Mesh sourceForCsg = src;
        if (originalMeshMap.ContainsKey(mf)) sourceForCsg = originalMeshMap[mf]; else originalMeshMap[mf] = src;
        Mesh result = TryRuntimeCsgSubtract(sourceForCsg, mf.transform, unionSubtractor, transform);
        if (result != null)
        {
          // Preserve original materials/shader on this mesh's renderer
          try
          {
            var rend = mf.GetComponent<Renderer>();
            if (rend != null && !originalMaterialMap.ContainsKey(mf)) originalMaterialMap[mf] = rend.sharedMaterials;
          }
          catch { }
          mf.mesh = result;
          // Post-process result to ensure normals/tangents/uv2 are present
          try { PostProcessCSGResult(result); } catch { }
          try { var mc = mf.GetComponent<MeshCollider>(); if (mc != null) { mc.sharedMesh = null; mc.sharedMesh = result; } } catch { }
          // Reapply saved materials so shader is retained
          try { var rend2 = mf.GetComponent<Renderer>(); if (rend2 != null && originalMaterialMap.ContainsKey(mf)) rend2.sharedMaterials = originalMaterialMap[mf]; } catch { }
          modified++;
        }
      }
      catch (System.Exception ex)
      {
        Debug.LogWarning("WallCutout: LibCSG subtraction failed for meshfilter " + (mf != null ? mf.name : "<null>") + ": " + ex.Message);
      }
    }
    return modified;
  }

  // Schedule a delayed/batched CSG application to avoid performing many heavy CSG ops in quick succession
  void ScheduleCSGApply()
  {
    if (csgPending) return;
    csgPending = true;
    if (csgCoroutine != null) try { StopCoroutine(csgCoroutine); } catch { }
    csgCoroutine = StartCoroutine(DelayedApplyCSG());
  }

  System.Collections.IEnumerator DelayedApplyCSG()
  {
    float t = 0f;
    while (t < csgBatchDelay)
    {
      t += Time.deltaTime;
      yield return null;
    }
    ApplyPendingCSG();
    csgPending = false;
    csgCoroutine = null;
  }

  void ApplyPendingCSG()
  {
    try
    {
      // Attempt to subtract using the accumulated subtractor meshes
      int modified = TrySubtractUsingLibCSGOnChildren(null, activeSubtractorMeshes);
      if (modified > 0)
      {
        Debug.Log($"WallCutout: Batched LibCSG subtraction modified {modified} mesh filters. Removing intersecting visual fallbacks.");
        // Remove any spawned visual fallback cutouts that intersect union of subtractors
        try { RemoveSpawnedCutoutsIntersecting(activeSubtractorMeshes); } catch { }
        // Clear subtractors list after application
        activeSubtractorMeshes.Clear();
      }
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: ApplyPendingCSG failed: " + ex.Message);
    }
  }

  // Compute world-space bounds of a local bounds by transforming 8 corners
  Bounds GetWorldBoundsFromLocal(Bounds b, Transform t)
  {
    Vector3[] corners = new Vector3[8];
    Vector3 cmin = b.min; Vector3 cmax = b.max;
    corners[0] = new Vector3(cmin.x, cmin.y, cmin.z);
    corners[1] = new Vector3(cmax.x, cmin.y, cmin.z);
    corners[2] = new Vector3(cmin.x, cmax.y, cmin.z);
    corners[3] = new Vector3(cmax.x, cmax.y, cmin.z);
    corners[4] = new Vector3(cmin.x, cmin.y, cmax.z);
    corners[5] = new Vector3(cmax.x, cmin.y, cmax.z);
    corners[6] = new Vector3(cmin.x, cmax.y, cmax.z);
    corners[7] = new Vector3(cmax.x, cmax.y, cmax.z);
    Bounds wb = new Bounds(t.TransformPoint(corners[0]), Vector3.zero);
    for (int i = 1; i < 8; i++) wb.Encapsulate(t.TransformPoint(corners[i]));
    return wb;
  }

  void RemoveSpawnedCutoutsIntersecting(System.Collections.Generic.List<Mesh> subtractMeshes)
  {
    if (subtractMeshes == null || subtractMeshes.Count == 0) return;
    Mesh union = MergeMeshes(subtractMeshes);
    if (union == null) return;
    Bounds ub = GetWorldBoundsFromLocal(union.bounds, this.transform);
    for (int i = spawnedCutouts.Count - 1; i >= 0; i--)
    {
      var go = spawnedCutouts[i];
      if (go == null) { spawnedCutouts.RemoveAt(i); continue; }
      try
      {
        var rend = go.GetComponentInChildren<Renderer>();
        if (rend != null && rend.bounds.Intersects(ub))
        {
          spawnedCutouts.RemoveAt(i);
          try { Destroy(go); } catch { try { DestroyImmediate(go); } catch { } }
        }
      }
      catch { }
    }
  }

  // Simplify mesh using vertex clustering (grid-based). This is a fast but lossy method suitable for runtime.
  Mesh SimplifyMeshByVertexClustering(Mesh src, float cellSize)
  {
    if (src == null) return null;
    if (cellSize <= 0f) return src;
    try
    {
      var verts = src.vertices;
      var uvs = src.uv != null && src.uv.Length == verts.Length ? src.uv : null;
      var norms = src.normals != null && src.normals.Length == verts.Length ? src.normals : null;
      int[] tris = src.triangles;

      var map = new System.Collections.Generic.Dictionary<long, int>();
      var newVerts = new System.Collections.Generic.List<Vector3>();
      var newUVs = uvs != null ? new System.Collections.Generic.List<Vector2>() : null;
      var newNormals = norms != null ? new System.Collections.Generic.List<Vector3>() : null;
      int[] vertexMap = new int[verts.Length];

      System.Func<Vector3, long> keyOf = (v) =>
      {
        long xi = (long)System.Math.Floor(v.x / cellSize);
        long yi = (long)System.Math.Floor(v.y / cellSize);
        long zi = (long)System.Math.Floor(v.z / cellSize);
        return (xi & 0x1FFFFF) | ((yi & 0x1FFFFF) << 21) | ((zi & 0x1FFFFF) << 42);
      };

      for (int i = 0; i < verts.Length; i++)
      {
        long key = keyOf(verts[i]);
        if (!map.TryGetValue(key, out int idx))
        {
          idx = newVerts.Count;
          map[key] = idx;
          newVerts.Add(verts[i]);
          if (newUVs != null) newUVs.Add(uvs[i]);
          if (newNormals != null) newNormals.Add(norms[i]);
        }
        vertexMap[i] = idx;
      }

      var newTris = new System.Collections.Generic.List<int>(tris.Length);
      for (int i = 0; i < tris.Length; i += 3)
      {
        int a = vertexMap[tris[i]], b = vertexMap[tris[i + 1]], c = vertexMap[tris[i + 2]];
        if (a == b || b == c || a == c) continue;
        newTris.Add(a); newTris.Add(b); newTris.Add(c);
      }

      if (newVerts.Count == 0 || newTris.Count == 0) return null;
      Mesh outM = new Mesh(); outM.vertices = newVerts.ToArray(); outM.triangles = newTris.ToArray();
      try { if (newUVs != null) outM.uv = newUVs.ToArray(); } catch { }
      try { if (newNormals != null) outM.normals = newNormals.ToArray(); else outM.RecalculateNormals(); } catch { outM.RecalculateNormals(); }
      outM.RecalculateBounds();
      try { outM.RecalculateTangents(); } catch { try { EnsureMeshHasTangents(outM); } catch { } }
      return outM;
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: SimplifyMeshByVertexClustering failed: " + ex.Message);
      return null;
    }
  }

  // Post-process mesh: clean, recalc normals/tangents, ensure uv2 if needed for lightmaps
  void ApplyPostProcessToMesh(Mesh m)
  {
    if (m == null) return;
    try
    {
      // Clean small degenerate geometry
      CleanMesh(m);
      // Recalculate normals only if missing or invalid (to avoid clobbering transferred normals)
      try
      {
        bool recalcNormals = false;
        if (m.normals == null || m.normals.Length != m.vertexCount) recalcNormals = true;
        else
        {
          float maxMag = 0f; foreach (var n in m.normals) { maxMag = Mathf.Max(maxMag, n.magnitude); }
          if (maxMag < 1e-4f) recalcNormals = true;
        }
        if (recalcNormals) m.RecalculateNormals();
      }
      catch { }
      // Recalculate tangents or compute them
      try
      {
        bool recalcTangents = false;
        if (m.tangents == null || m.tangents.Length != m.vertexCount) recalcTangents = true;
        if (recalcTangents) { try { m.RecalculateTangents(); } catch { try { EnsureMeshHasTangents(m); } catch { } } }
      }
      catch { try { EnsureMeshHasTangents(m); } catch { } }
      // If uv2 (lightmap UV) missing, attempt to copy uv into uv2 as a fallback (not ideal but preserves something)
      try { if (m.uv2 == null || m.uv2.Length != m.vertexCount) m.uv2 = m.uv; } catch { }
    }
    catch (System.Exception ex) { Debug.LogWarning("WallCutout: ApplyPostProcessToMesh failed: " + ex.Message); }
  }

  // Specialized post-processing for CSG results: clean, smooth normals with mid-angle, ensure tangents and uv2
  void PostProcessCSGResult(Mesh m)
  {
    if (m == null) return;
    try
    {
      // Clean degenerate geometry first
      CleanMesh(m);
      // Smooth normals with a moderate threshold to reduce faceting introduced by boolean ops
      try { GenerateSmoothNormals(m, 45f); } catch { try { m.RecalculateNormals(); } catch { } }
      // Ensure tangents (required for normal maps)
      try { m.RecalculateTangents(); } catch { try { EnsureMeshHasTangents(m); } catch { } }
      // As fallback, ensure uv2 exists
      try { if (m.uv2 == null || m.uv2.Length != m.vertexCount) m.uv2 = m.uv; } catch { }
      // Final bounds/tangent sanity
      try { m.RecalculateBounds(); } catch { }
    }
    catch (System.Exception ex) { Debug.LogWarning("WallCutout: PostProcessCSGResult failed: " + ex.Message); }
  }

  void ApplyTransparentMaterials(GameObject root, bool onTop, float alpha)
  {
    if (root == null) return;
    try
    {
      var renderers = root.GetComponentsInChildren<Renderer>(true);
      foreach (var r in renderers)
      {
        if (r == null) continue;
        Material[] mats = r.sharedMaterials;
        Material[] newMats = new Material[mats.Length];
        for (int i = 0; i < mats.Length; i++)
        {
          Material src = mats[i];
          if (src == null)
          {
            newMats[i] = null; continue;
          }
          // Clone material to avoid modifying asset
          Material clone = new Material(src);
          // Attempt to set to transparent blending for Standard shader
          try
          {
            if (clone.HasProperty("_Mode"))
            {
              // Standard shader: set rendering mode via keywords/properties
              clone.SetFloat("_Mode", 3); // 3 = Transparent
              clone.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
              clone.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
              clone.SetInt("_ZWrite", 0);
              clone.DisableKeyword("_ALPHATEST_ON");
              clone.DisableKeyword("_ALPHABLEND_ON");
              clone.EnableKeyword("_ALPHAPREMULTIPLY_ON");
              clone.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
          }
          catch { }
          // Apply alpha if color property exists
          try { if (clone.HasProperty("_Color")) { Color c = clone.color; c.a = Mathf.Clamp01(alpha); clone.color = c; } }
          catch { }
          // Optionally force to render on top by raising renderQueue
          if (onTop)
          {
            try { clone.renderQueue = Mathf.Max(clone.renderQueue, 4000); } catch { }
          }
          newMats[i] = clone;
        }
        r.sharedMaterials = newMats;
      }
    }
    catch (System.Exception ex) { Debug.LogWarning("WallCutout: ApplyTransparentMaterials failed: " + ex.Message); }
  }

  // Legacy triangle-removal and subdivision helpers removed in favor of LibCSG-based subtraction.

  // Winding-agnostic point-in-polygon test (ray-crossing)
  bool PointInPolygon(Vector2 p, List<Vector2> poly)
  {
    bool inside = false;
    for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
    {
      Vector2 pi = poly[i]; Vector2 pj = poly[j];
      bool intersect = ((pi.y > p.y) != (pj.y > p.y)) && (p.x < (pj.x - pi.x) * (p.y - pi.y) / (pj.y - pi.y + 1e-12f) + pi.x);
      if (intersect) inside = !inside;
    }
    return inside;
  }

  // Remove all spawned transparent cutouts for this wall
  public void RemoveAllCutouts()
  {
    for (int i = spawnedCutouts.Count - 1; i >= 0; i--)
    {
      var go = spawnedCutouts[i];
      if (go != null) DestroyImmediate(go);
    }
    spawnedCutouts.Clear();

    // Restore original meshes
    try
    {
      foreach (var kv in originalMeshMap)
      {
        var mf = kv.Key; var mesh = kv.Value;
        if (mf != null && mesh != null)
        {
          mf.mesh = mesh;
          try { ApplyPostProcessToMesh(mesh); } catch { }
        }
      }
    }
    catch { }
    // Restore original materials for any modified renderers
    try
    {
      foreach (var kv in originalMaterialMap)
      {
        var mf = kv.Key; var mats = kv.Value;
        try { if (mf != null) { var rend = mf.GetComponent<Renderer>(); if (rend != null && mats != null) rend.sharedMaterials = mats; } } catch { }
      }
    }
    catch { }
    originalMeshMap.Clear();
    originalMaterialMap.Clear();
    activeSubtractorMeshes.Clear();
  }

  void WeldVertices(ref Mesh m, float threshold)
  {
    if (m == null) return;
    try
    {
      Vector3[] verts = m.vertices; int[] tris = m.triangles; List<Vector3> newVerts = new List<Vector3>(); int[] mapping = new int[verts.Length];
      for (int i = 0; i < verts.Length; i++) { int found = -1; for (int j = 0; j < newVerts.Count; j++) { if ((newVerts[j] - verts[i]).sqrMagnitude <= threshold * threshold) { found = j; break; } } if (found == -1) { mapping[i] = newVerts.Count; newVerts.Add(verts[i]); } else mapping[i] = found; }
      int[] newTris = new int[tris.Length]; for (int i = 0; i < tris.Length; i++) newTris[i] = mapping[tris[i]];
      m.Clear(); m.vertices = newVerts.ToArray(); m.triangles = newTris; try { m.uv = m.uv; } catch { }
      m.RecalculateBounds(); m.RecalculateNormals();
    }
    catch (System.Exception ex) { Debug.LogWarning("WallCutout: WeldVertices failed: " + ex.Message); }
  }

  // Attempt to map UVs and normals from the source mesh to the newly generated CSG mesh by nearest vertex mapping.
  // This is a best-effort approach to reduce visible texture discontinuities after subtraction.
  void TransferUVsAndNormalsFromSource(Mesh outMesh, Mesh srcMesh)
  {
    if (outMesh == null || srcMesh == null) return;
    try
    {
      Vector3[] outV = outMesh.vertices;
      Vector3[] srcV = srcMesh.vertices;
      Vector3[] srcN = srcMesh.normals != null && srcMesh.normals.Length == srcMesh.vertexCount ? srcMesh.normals : null;
      Vector2[] srcUV = srcMesh.uv != null && srcMesh.uv.Length == srcMesh.vertexCount ? srcMesh.uv : null;
      if ((srcUV == null || srcUV.Length == 0) && (srcN == null || srcN.Length == 0)) return;

      Vector2[] newUV = new Vector2[outV.Length];
      Vector3[] newNormals = new Vector3[outV.Length];

      // Safety: avoid O(n*m) explosion. If product is very large, skip mapping.
      long estimateCost = (long)outV.Length * (long)srcV.Length;
      const long maxCost = 5000000; // threshold (tunable)
      if (estimateCost > maxCost)
      {
        Debug.LogWarning($"WallCutout: Skipping UV transfer because mapping cost {estimateCost} exceeds threshold {maxCost}. Falling back to generated UVs.");
        return;
      }

      int triCount = srcMesh.triangles != null ? srcMesh.triangles.Length / 3 : 0;
      bool tryTriangleInterp = srcUV != null && triCount > 0 && triCount <= 20000; // limit triangles for safety

      // Build spatial buckets based on source bounds for triangles (if using triangle interpolation) and vertices
      Bounds b = srcMesh.bounds;
      float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
      int cellsPerAxis = Mathf.Clamp(Mathf.CeilToInt(Mathf.Pow(srcV.Length / 100f, 1f / 3f)), 4, 64);
      Vector3 cellSizeV = new Vector3(maxDim / cellsPerAxis, maxDim / cellsPerAxis, maxDim / cellsPerAxis);

      var vertBuckets = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>();
      for (int j = 0; j < srcV.Length; j++)
      {
        Vector3 rel = srcV[j] - b.min;
        int xi = Mathf.Clamp((int)(rel.x / cellSizeV.x), 0, cellsPerAxis - 1);
        int yi = Mathf.Clamp((int)(rel.y / cellSizeV.y), 0, cellsPerAxis - 1);
        int zi = Mathf.Clamp((int)(rel.z / cellSizeV.z), 0, cellsPerAxis - 1);
        long key = ((long)xi << 42) | ((long)yi << 21) | (long)zi;
        if (!vertBuckets.TryGetValue(key, out var list)) { list = new System.Collections.Generic.List<int>(); vertBuckets[key] = list; }
        list.Add(j);
      }

      System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>> triBuckets = null;
      int[] srcTris = srcMesh.triangles;
      if (tryTriangleInterp)
      {
        triBuckets = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>();
        for (int t = 0; t < srcTris.Length; t += 3)
        {
          int a = srcTris[t], c = srcTris[t + 1], d = srcTris[t + 2];
          Vector3 centroid = (srcV[a] + srcV[c] + srcV[d]) / 3f;
          Vector3 rel = centroid - b.min;
          int xi = Mathf.Clamp((int)(rel.x / cellSizeV.x), 0, cellsPerAxis - 1);
          int yi = Mathf.Clamp((int)(rel.y / cellSizeV.y), 0, cellsPerAxis - 1);
          int zi = Mathf.Clamp((int)(rel.z / cellSizeV.z), 0, cellsPerAxis - 1);
          long key = ((long)xi << 42) | ((long)yi << 21) | (long)zi;
          if (!triBuckets.TryGetValue(key, out var list)) { list = new System.Collections.Generic.List<int>(); triBuckets[key] = list; }
          list.Add(t);
        }
      }

      // Helper to compute barycentric coords of p relative to triangle a,b,c
      System.Func<Vector3, Vector3, Vector3, Vector3, (float, float, float)> bary = (p, a, b2, c2) =>
      {
        Vector3 v0 = b2 - a; Vector3 v1 = c2 - a; Vector3 v2 = p - a;
        float d00 = Vector3.Dot(v0, v0);
        float d01 = Vector3.Dot(v0, v1);
        float d11 = Vector3.Dot(v1, v1);
        float d20 = Vector3.Dot(v2, v0);
        float d21 = Vector3.Dot(v2, v1);
        float denom = d00 * d11 - d01 * d01;
        if (Mathf.Abs(denom) < 1e-9f) return (float.NaN, float.NaN, float.NaN);
        float v = (d11 * d20 - d01 * d21) / denom;
        float w = (d00 * d21 - d01 * d20) / denom;
        float u = 1f - v - w;
        return (u, v, w);
      };

      // For each out vertex: try triangle interpolation first, then fall back to vertex mapping
      for (int i = 0; i < outV.Length; i++)
      {
        bool assigned = false;
        Vector3 p = outV[i];
        if (tryTriangleInterp && triBuckets != null)
        {
          Vector3 rel = p - b.min; int xi = Mathf.Clamp((int)(rel.x / cellSizeV.x), 0, cellsPerAxis - 1); int yi = Mathf.Clamp((int)(rel.y / cellSizeV.y), 0, cellsPerAxis - 1); int zi = Mathf.Clamp((int)(rel.z / cellSizeV.z), 0, cellsPerAxis - 1);
          for (int dx = -1; dx <= 1 && !assigned; dx++) for (int dy = -1; dy <= 1 && !assigned; dy++) for (int dz = -1; dz <= 1 && !assigned; dz++)
              {
                int nx = Mathf.Clamp(xi + dx, 0, cellsPerAxis - 1);
                int ny = Mathf.Clamp(yi + dy, 0, cellsPerAxis - 1);
                int nz = Mathf.Clamp(zi + dz, 0, cellsPerAxis - 1);
                long key = ((long)nx << 42) | ((long)ny << 21) | (long)nz;
                if (!triBuckets.TryGetValue(key, out var list)) continue;
                for (int li = 0; li < list.Count && !assigned; li++)
                {
                  int t = list[li]; int a = srcTris[t], b2 = srcTris[t + 1], c2 = srcTris[t + 2];
                  var weights = bary(p, srcV[a], srcV[b2], srcV[c2]);
                  float u = weights.Item1, v2w = weights.Item2, w = weights.Item3;
                  if (float.IsNaN(u)) continue;
                  const float eps = -0.01f; // allow slight negatives
                  if (u >= eps && v2w >= eps && w >= eps)
                  {
                    if (srcUV != null) newUV[i] = srcUV[a] * u + srcUV[b2] * v2w + srcUV[c2] * w;
                    if (srcN != null) newNormals[i] = srcN[a] * u + srcN[b2] * v2w + srcN[c2] * w;
                    assigned = true; break;
                  }
                }
              }
        }

        if (!assigned)
        {
          // Fallback nearest-vertex (search vert bucket neighbors)
          Vector3 rel = p - b.min; int xi = Mathf.Clamp((int)(rel.x / cellSizeV.x), 0, cellsPerAxis - 1); int yi = Mathf.Clamp((int)(rel.y / cellSizeV.y), 0, cellsPerAxis - 1); int zi = Mathf.Clamp((int)(rel.z / cellSizeV.z), 0, cellsPerAxis - 1);
          int best = -1; float bestDist = float.MaxValue;
          for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
              {
                int nx = Mathf.Clamp(xi + dx, 0, cellsPerAxis - 1);
                int ny = Mathf.Clamp(yi + dy, 0, cellsPerAxis - 1);
                int nz = Mathf.Clamp(zi + dz, 0, cellsPerAxis - 1);
                long key = ((long)nx << 42) | ((long)ny << 21) | (long)nz;
                if (!vertBuckets.TryGetValue(key, out var list)) continue;
                for (int k = 0; k < list.Count; k++)
                {
                  int j = list[k]; float d = (p - srcV[j]).sqrMagnitude; if (d < bestDist) { bestDist = d; best = j; }
                }
              }
          if (best != -1)
          {
            if (srcUV != null && srcUV.Length > best) newUV[i] = srcUV[best];
            if (srcN != null && srcN.Length > best) newNormals[i] = srcN[best];
          }
        }
      }

      try { outMesh.uv = newUV; } catch { }
      try { outMesh.normals = newNormals; } catch { outMesh.RecalculateNormals(); }
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: TransferUVsAndNormalsFromSource failed: " + ex.Message);
    }
  }

  bool IsLibCsgAvailable()
  {
    // quick check for LibCSG types
    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
    {
      if (asm.GetType("LibCSG.CSGBrush") != null) return true;
    }
    return false;
  }

  // Simple mesh cleanup: weld very-close vertices and remove degenerate triangles to reduce z-fighting/overlay
  void CleanMesh(Mesh m)
  {
    if (m == null) return;
    try
    {
      // Parameters
      float weldThreshold = 1e-4f;
      float uvThreshold = 1e-3f;

      Vector3[] verts = m.vertices;
      Vector2[] uvs = m.uv != null && m.uv.Length == verts.Length ? m.uv : null;
      Vector3[] normals = m.normals != null && m.normals.Length == verts.Length ? m.normals : null;
      Vector4[] tangents = m.tangents != null && m.tangents.Length == verts.Length ? m.tangents : null;
      int[] tris = m.triangles;

      // Spatial hash for faster proximity tests
      var buckets = new Dictionary<long, List<int>>();
      float cell = weldThreshold * 2f;
      System.Func<Vector3, long> keyOf = (v) =>
      {
        long xi = (long)System.Math.Floor(v.x / cell);
        long yi = (long)System.Math.Floor(v.y / cell);
        long zi = (long)System.Math.Floor(v.z / cell);
        return (xi & 0x1FFFFF) | ((yi & 0x1FFFFF) << 21) | ((zi & 0x1FFFFF) << 42);
      };

      List<Vector3> newVerts = new List<Vector3>(verts.Length);
      List<Vector2> newUVs = uvs != null ? new List<Vector2>(verts.Length) : null;
      List<Vector3> newNormals = normals != null ? new List<Vector3>(verts.Length) : null;
      List<Vector4> newTangents = tangents != null ? new List<Vector4>(verts.Length) : null;
      int[] map = new int[verts.Length];

      for (int i = 0; i < verts.Length; i++)
      {
        var v = verts[i];
        long key = keyOf(v);
        int found = -1;
        List<int> list;
        if (buckets.TryGetValue(key, out list))
        {
          for (int idx = 0; idx < list.Count; idx++)
          {
            int j = list[idx];
            if ((newVerts[j] - v).sqrMagnitude <= weldThreshold * weldThreshold)
            {
              // check UV similarity when present to preserve UV seams
              if (uvs != null)
              {
                if ((newUVs[j] - uvs[i]).sqrMagnitude > uvThreshold * uvThreshold) continue;
              }
              found = j; break;
            }
          }
        }
        if (found == -1)
        {
          found = newVerts.Count;
          newVerts.Add(v);
          if (newUVs != null) newUVs.Add(uvs[i]);
          if (newNormals != null) newNormals.Add(normals[i]);
          if (newTangents != null) newTangents.Add(tangents[i]);
          if (list == null) { list = new List<int>(); buckets[key] = list; }
          list.Add(found);
        }
        map[i] = found;
      }

      // Remap triangles and skip degenerate
      List<int> outTris = new List<int>(tris.Length);
      for (int i = 0; i + 2 < tris.Length; i += 3)
      {
        int a = map[tris[i]];
        int b = map[tris[i + 1]];
        int c = map[tris[i + 2]];
        if (a == b || b == c || a == c) continue;
        Vector3 va = newVerts[a], vb = newVerts[b], vc = newVerts[c];
        if (((vb - va).sqrMagnitude < 1e-10f) || ((vc - vb).sqrMagnitude < 1e-10f) || ((va - vc).sqrMagnitude < 1e-10f)) continue;
        outTris.Add(a); outTris.Add(b); outTris.Add(c);
      }

      // Apply to mesh
      m.Clear();
      m.vertices = newVerts.ToArray();
      if (newUVs != null) m.uv = newUVs.ToArray();
      if (newTangents != null) m.tangents = newTangents.ToArray();
      m.triangles = outTris.ToArray();

      // Recalculate smooth normals with a reasonable default angle
      GenerateSmoothNormals(m, 60f);
      m.RecalculateBounds();
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: CleanMesh failed: " + ex.Message);
    }
  }

  // Generate per-vertex smooth normals using an angle threshold (in degrees).
  // This averages face normals for incident triangles whose angle difference is within threshold.
  void GenerateSmoothNormals(Mesh m, float angleThresholdDeg)
  {
    if (m == null) return;
    var verts = m.vertices;
    var tris = m.triangles;
    int vCount = verts.Length;
    int tCount = tris.Length / 3;
    if (tCount == 0 || vCount == 0) return;

    Vector3[] faceNormals = new Vector3[tCount];
    float[] faceAreas = new float[tCount];
    for (int i = 0; i < tCount; i++)
    {
      var a = verts[tris[i * 3 + 0]];
      var b = verts[tris[i * 3 + 1]];
      var c = verts[tris[i * 3 + 2]];
      Vector3 n = Vector3.Cross(b - a, c - a);
      float area = n.magnitude * 0.5f;
      if (n.sqrMagnitude > 1e-12f) n.Normalize(); else n = Vector3.up;
      faceNormals[i] = n;
      faceAreas[i] = area;
    }

    // Build adjacency: for each vertex, list incident triangle indices
    List<int>[] incident = new List<int>[vCount];
    for (int i = 0; i < vCount; i++) incident[i] = new List<int>();
    for (int t = 0; t < tCount; t++)
    {
      incident[tris[t * 3 + 0]].Add(t);
      incident[tris[t * 3 + 1]].Add(t);
      incident[tris[t * 3 + 2]].Add(t);
    }

    float cosThreshold = Mathf.Cos(angleThresholdDeg * Mathf.Deg2Rad);
    Vector3[] outNormals = new Vector3[vCount];

    for (int vi = 0; vi < vCount; vi++)
    {
      var list = incident[vi];
      if (list.Count == 0) { outNormals[vi] = Vector3.up; continue; }
      Vector3 accum = Vector3.zero; float total = 0f;
      for (int i = 0; i < list.Count; i++)
      {
        Vector3 fn = faceNormals[list[i]];
        float w = faceAreas[list[i]];
        // include faces whose normals are within angle threshold relative to this face
        for (int j = 0; j < list.Count; j++)
        {
          Vector3 other = faceNormals[list[j]];
          if (Vector3.Dot(fn, other) >= cosThreshold)
          {
            accum += other * faceAreas[list[j]];
            total += faceAreas[list[j]];
          }
        }
      }
      if (total <= 0f) outNormals[vi] = Vector3.up; else outNormals[vi] = (accum / total).normalized;
    }

    m.normals = outNormals;
  }

  // Ensure mesh has tangents computed from UVs and normals so shaders with normal maps render correctly.
  void EnsureMeshHasTangents(Mesh m)
  {
    if (m == null) return;
    try
    {
      var verts = m.vertices;
      var norms = m.normals;
      var uvs = m.uv;
      var tris = m.triangles;
      if (uvs == null || uvs.Length != verts.Length || norms == null || norms.Length != verts.Length || tris == null) return;

      Vector3[] tan1 = new Vector3[verts.Length];
      Vector3[] tan2 = new Vector3[verts.Length];
      Vector4[] tangents = new Vector4[verts.Length];

      for (int i = 0; i + 2 < tris.Length; i += 3)
      {
        int i1 = tris[i];
        int i2 = tris[i + 1];
        int i3 = tris[i + 2];

        Vector3 v1 = verts[i1]; Vector3 v2 = verts[i2]; Vector3 v3 = verts[i3];
        Vector2 w1 = uvs[i1]; Vector2 w2 = uvs[i2]; Vector2 w3 = uvs[i3];

        float x1 = v2.x - v1.x;
        float x2 = v3.x - v1.x;
        float y1 = v2.y - v1.y;
        float y2 = v3.y - v1.y;
        float z1 = v2.z - v1.z;
        float z2 = v3.z - v1.z;

        float s1 = w2.x - w1.x;
        float s2 = w3.x - w1.x;
        float t1 = w2.y - w1.y;
        float t2 = w3.y - w1.y;

        float r = (s1 * t2 - s2 * t1);
        if (Mathf.Abs(r) < 1e-8f) r = 1f; else r = 1.0f / r;
        Vector3 sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
        Vector3 tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);

        tan1[i1] += sdir; tan1[i2] += sdir; tan1[i3] += sdir;
        tan2[i1] += tdir; tan2[i2] += tdir; tan2[i3] += tdir;
      }

      for (int a = 0; a < verts.Length; a++)
      {
        Vector3 n = norms[a]; Vector3 t = tan1[a];
        // Gram-Schmidt orthogonalize
        Vector3.OrthoNormalize(ref n, ref t);
        tangents[a].x = t.x; tangents[a].y = t.y; tangents[a].z = t.z;
        // Calculate handedness
        tangents[a].w = (Vector3.Dot(Vector3.Cross(n, t), tan2[a]) < 0.0f) ? -1.0f : 1.0f;
      }
      m.tangents = tangents;
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning("WallCutout: EnsureMeshHasTangents failed: " + ex.Message);
    }
  }
}