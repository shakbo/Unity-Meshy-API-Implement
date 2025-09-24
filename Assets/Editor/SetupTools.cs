// Editor utility: Tools -> InstaSpace -> Setup MVP (Create Prefabs, Materials, Anchors)
// Creates Assets/InstaspaceAuto (Prefabs, Materials), 5 placeholder prefabs, PreviewMaterial,
// LeftControllerAnchor & RightControllerAnchor in scene, and tries to attach/wire MVP scripts if found.
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;
using UnityEditor.SceneManagement;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

public static class SetupTools
{
  [MenuItem("Tools/InstaSpace/Setup MVP (Create Prefabs, Materials, Anchors)")]
  public static void SetupMVP()
  {
    // Ensure no compile errors first - Unity must finish compiling to show menu
    // Create folders
    string basePath = "Assets/InstaspaceAuto";
    string prefabsPath = Path.Combine(basePath, "Prefabs");
    string matsPath = Path.Combine(basePath, "Materials");
    Directory.CreateDirectory(basePath);
    Directory.CreateDirectory(prefabsPath);
    Directory.CreateDirectory(matsPath);

    AssetDatabase.StartAssetEditing();
    try
    {
      // Create preview material
      string matAssetPath = Path.Combine(matsPath, "PreviewMaterial.mat").Replace("\\", "/");
      Material previewMat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);
      if (previewMat == null)
      {
        // Try URP first, then fallback to Standard, then Unlit
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        Shader standardShader = Shader.Find("Standard");
        Shader unlitShader = Shader.Find("Unlit/Color");

        Shader shaderToUse = urpLit ?? standardShader ?? unlitShader;

        if (shaderToUse == null)
        {
          Debug.LogError("[InstaSpace] No suitable shader found for preview material!");
          return;
        }

        previewMat = new Material(shaderToUse);

        // Configure transparency based on shader type
        if (shaderToUse == urpLit)
        {
          // URP Lit shader setup for transparency
          previewMat.SetFloat("_Surface", 1); // Transparent
          previewMat.SetFloat("_Blend", 0); // Alpha blending
          previewMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
          previewMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
          previewMat.SetInt("_ZWrite", 0);
          previewMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
          previewMat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
          previewMat.renderQueue = 3000;
        }
        else if (shaderToUse == standardShader)
        {
          // Standard shader setup for transparency
          previewMat.SetFloat("_Mode", 3); // Transparent mode
          previewMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
          previewMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
          previewMat.SetInt("_ZWrite", 0);
          previewMat.EnableKeyword("_ALPHABLEND_ON");
          previewMat.renderQueue = 3000;
        }

        // Set color with transparency
        Color transparentColor = new Color(0.8f, 0.8f, 1f, 0.6f); // Light blue with alpha
        if (previewMat.HasProperty("_BaseColor"))
          previewMat.SetColor("_BaseColor", transparentColor); // URP
        else if (previewMat.HasProperty("_Color"))
          previewMat.SetColor("_Color", transparentColor); // Standard/Unlit

        AssetDatabase.CreateAsset(previewMat, matAssetPath);
        Debug.Log($"[InstaSpace] Created preview material using shader: {shaderToUse.name}");
      }

      // Create LineRenderer material for RayVisualizer
      string lineMaterialPath = Path.Combine(matsPath, "RayLineMaterial.mat").Replace("\\", "/");
      Material lineMat = AssetDatabase.LoadAssetAtPath<Material>(lineMaterialPath);
      if (lineMat == null)
      {
        // For LineRenderer, prioritize shaders that work well with lines
        Shader lineShader = Shader.Find("Sprites/Default") ??
                           Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ??
                           Shader.Find("Universal Render Pipeline/Unlit") ??
                           Shader.Find("Unlit/Color") ??
                           Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply") ??
                           Shader.Find("Hidden/Internal-Colored");

        lineMat = new Material(lineShader);
        Color lineColor = Color.cyan;

        // Set color properties based on shader
        if (lineMat.HasProperty("_Color"))
          lineMat.SetColor("_Color", lineColor); // Most common property
        else if (lineMat.HasProperty("_BaseColor"))
          lineMat.SetColor("_BaseColor", lineColor); // URP
        else if (lineMat.HasProperty("_TintColor"))
          lineMat.SetColor("_TintColor", lineColor); // Particles

        // Configure for transparency
        if (lineMat.HasProperty("_Mode"))
          lineMat.SetFloat("_Mode", 2); // Transparent mode for Built-in

        lineMat.renderQueue = 3000; // Transparent queue

        AssetDatabase.CreateAsset(lineMat, lineMaterialPath);
        Debug.Log($"[InstaSpace] Created ray line material using shader: {lineShader.name}");
      }

      // Create 5 placeholder prefabs (simple cubes)
      GameObject[] createdPrefabs = new GameObject[5];
      for (int i = 0; i < 5; i++)
      {
        string prefabName = $"Placeholder_{i + 1}.prefab";
        string fullPrefabPath = Path.Combine(prefabsPath, prefabName).Replace("\\", "/");
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(fullPrefabPath);
        if (existing != null)
        {
          createdPrefabs[i] = existing;
          continue;
        }

        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        tmp.name = $"Placeholder_{i + 1}";
        tmp.transform.localScale = Vector3.one * 0.2f;

        // Create a simple colored material for each placeholder (not transparent)
        var rend = tmp.GetComponent<Renderer>();
        if (rend != null)
        {
          // Create and save material asset for each prefab
          string matName = $"PlaceholderMaterial_{i + 1}.mat";
          string matPath = Path.Combine(matsPath, matName).Replace("\\", "/");

          Material prefabMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
          if (prefabMat == null)
          {
            // Use default material or create a simple colored one
            Shader shaderToUse = Shader.Find("Universal Render Pipeline/Lit") ??
                                 Shader.Find("Standard") ??
                                 Shader.Find("Unlit/Color");

            if (shaderToUse == null)
            {
              Debug.LogWarning($"[InstaSpace] No suitable shader found for prefab {i + 1}, using default");
              prefabMat = new Material(Shader.Find("Sprites/Default")); // Fallback
            }
            else
            {
              prefabMat = new Material(shaderToUse);
            }

            // Assign different colors to distinguish placeholders
            Color[] colors = { Color.red, Color.green, Color.blue, Color.yellow, Color.cyan };
            Color prefabColor = colors[i % colors.Length];

            if (prefabMat.HasProperty("_BaseColor"))
              prefabMat.SetColor("_BaseColor", prefabColor); // URP
            else if (prefabMat.HasProperty("_Color"))
              prefabMat.SetColor("_Color", prefabColor); // Standard/Unlit
            else if (prefabMat.HasProperty("_MainColor"))
              prefabMat.SetColor("_MainColor", prefabColor); // Some other shaders

            // Save material as asset
            AssetDatabase.CreateAsset(prefabMat, matPath);
            Debug.Log($"[InstaSpace] Created material: {matPath} with color {prefabColor}");
          }

          rend.sharedMaterial = prefabMat;
        }

        // Save as prefab
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(tmp, fullPrefabPath);
        createdPrefabs[i] = prefab;
        GameObject.DestroyImmediate(tmp);
        Debug.Log("[InstaSpace] Created prefab: " + fullPrefabPath);
      }

      // Ensure scene objects: LeftControllerAnchor / RightControllerAnchor
      GameObject leftAnchor = GameObject.Find("LeftControllerAnchor");
      if (leftAnchor == null)
      {
        leftAnchor = new GameObject("LeftControllerAnchor");
        Undo.RegisterCreatedObjectUndo(leftAnchor, "Create LeftControllerAnchor");
        Debug.Log("[InstaSpace] Created LeftControllerAnchor in scene.");
      }

      GameObject rightAnchor = GameObject.Find("RightControllerAnchor");
      if (rightAnchor == null)
      {
        rightAnchor = new GameObject("RightControllerAnchor");
        Undo.RegisterCreatedObjectUndo(rightAnchor, "Create RightControllerAnchor");
        Debug.Log("[InstaSpace] Created RightControllerAnchor in scene.");
      }

      // Try to attach known scripts (by full name or by searching assemblies)
      // helper to find Type by name (namespace included or not)
      Type FindType(string typeName)
      {
        var t = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
              try { return a.GetTypes(); } catch { return new Type[0]; }
            })
            .FirstOrDefault(ty => ty.FullName == typeName || ty.Name == typeName);
        return t;
      }

      // PanelUIBuilder on left
      Type panelType = FindType("InstaSpace.PanelUIBuilder") ?? FindType("PanelUIBuilder");
      if (panelType != null)
      {
        if (leftAnchor.GetComponent(panelType) == null)
        {
          leftAnchor.AddComponent(panelType);
          Debug.Log("[InstaSpace] Attached PanelUIBuilder to LeftControllerAnchor.");
        }
      }
      else
      {
        Debug.LogWarning("[InstaSpace] PanelUIBuilder type not found in project. Skipping attach.");
      }

      // ModelSelector on left
      Type selectorType = FindType("InstaSpace.ModelSelector") ?? FindType("ModelSelector");
      Component selectorComp = null;
      if (selectorType != null)
      {
        selectorComp = leftAnchor.GetComponent(selectorType) ?? leftAnchor.AddComponent(selectorType);
        Debug.Log("[InstaSpace] Attached ModelSelector to LeftControllerAnchor.");
      }
      else
      {
        Debug.LogWarning("[InstaSpace] ModelSelector type not found in project. Skipping attach.");
      }

      // RayVisualizer on right
      Type rayVisType = FindType("InstaSpace.RayVisualizer") ?? FindType("RayVisualizer");
      Component rayVisComp = null;
      if (rayVisType != null)
      {
        rayVisComp = rightAnchor.GetComponent(rayVisType);
        if (rayVisComp == null)
        {
          rayVisComp = rightAnchor.AddComponent(rayVisType);
          Debug.Log("[InstaSpace] Attached RayVisualizer to RightControllerAnchor.");
        }

        // Assign LineRenderer material
        FieldInfo lineMaterialField = rayVisType.GetField("lineMaterial", BindingFlags.Public | BindingFlags.Instance);
        if (lineMaterialField != null && lineMat != null)
        {
          lineMaterialField.SetValue(rayVisComp, lineMat);
          Debug.Log("[InstaSpace] Assigned LineRenderer material to RayVisualizer.");
        }
      }
      else
      {
        Debug.LogWarning("[InstaSpace] RayVisualizer type not found in project. Skipping attach.");
      }

      // PlacementManager on right
      Type placementType = FindType("InstaSpace.PlacementManager") ?? FindType("PlacementManager");
      Component placementComp = null;
      if (placementType != null)
      {
        placementComp = rightAnchor.GetComponent(placementType) ?? rightAnchor.AddComponent(placementType);
        Debug.Log("[InstaSpace] Attached PlacementManager to RightControllerAnchor.");
        // set public fields via reflection if possible
        if (placementComp != null)
        {
          FieldInfo previewMatField = placementType.GetField("previewMaterial", BindingFlags.Public | BindingFlags.Instance);
          if (previewMatField != null && previewMat != null)
            previewMatField.SetValue(placementComp, previewMat);

          FieldInfo rightControllerField = placementType.GetField("rightController", BindingFlags.Public | BindingFlags.Instance);
          if (rightControllerField != null)
            rightControllerField.SetValue(placementComp, rightAnchor.transform);

          // If there's a RayVisualizer field, assign
          FieldInfo rayVisField = placementType.GetField("rayVisualizer", BindingFlags.Public | BindingFlags.Instance);
          if (rayVisField != null && rayVisType != null)
          {
            var rvInstance = rightAnchor.GetComponent(rayVisType) ?? leftAnchor.GetComponent(rayVisType);
            if (rvInstance != null)
              rayVisField.SetValue(placementComp, rvInstance);
          }
        }
      }
      else
      {
        Debug.LogWarning("[InstaSpace] PlacementManager type not found in project. Skipping attach.");
      }

      // Wire modelPrefabs array on PanelUIBuilder and ModelSelector if present
      GameObject[] prefabAssets = createdPrefabs.Select(go => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(go))).ToArray();

      if (panelType != null)
      {
        var panelComp = leftAnchor.GetComponent(panelType);
        if (panelComp != null)
        {
          FieldInfo modelPrefabsField = panelType.GetField("modelPrefabs", BindingFlags.Public | BindingFlags.Instance);
          if (modelPrefabsField != null)
            modelPrefabsField.SetValue(panelComp, prefabAssets);
        }
      }

      if (selectorType != null)
      {
        var selComp = leftAnchor.GetComponent(selectorType);
        if (selComp != null)
        {
          FieldInfo modelPrefabsField = selectorType.GetField("modelPrefabs", BindingFlags.Public | BindingFlags.Instance);
          if (modelPrefabsField != null)
            modelPrefabsField.SetValue(selComp, prefabAssets);
          // Wire placement manager reference if possible
          FieldInfo placementField = selectorType.GetField("placementManager", BindingFlags.Public | BindingFlags.Instance);
          if (placementField != null && placementComp != null)
            placementField.SetValue(selComp, placementComp);
          // Wire panelUI reference if possible
          FieldInfo panelField = selectorType.GetField("panelUI", BindingFlags.Public | BindingFlags.Instance);
          var panelComp = leftAnchor.GetComponent(panelType);
          if (panelField != null && panelComp != null)
            panelField.SetValue(selComp, panelComp);
        }
      }

      // Mark scene dirty and save
      EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
      AssetDatabase.SaveAssets();
      AssetDatabase.Refresh();

      // Defer the dialog to the end of the editor frame to avoid nested OnGUI rendering errors.
      EditorApplication.delayCall += () => EditorUtility.DisplayDialog("InstaSpace Setup", "Setup complete. Created assets under Assets/InstaspaceAuto and attached scripts where possible.\n\nIf menu disappears, check Console for compile errors.", "OK");
    }
    catch (Exception ex)
    {
      Debug.LogError("[InstaSpace] Setup failed: " + ex);
      // Defer the error dialog as well to avoid GUI re-entrancy issues.
      EditorApplication.delayCall += () => EditorUtility.DisplayDialog("InstaSpace Setup - Error", "Setup failed. See Console for details.", "OK");
    }
    finally
    {
      AssetDatabase.StopAssetEditing();
      AssetDatabase.Refresh();
    }
  }

  [MenuItem("Tools/InstaSpace/Update Panel Size to Small")]
  public static void UpdatePanelSizeSmall()
  {
    UpdatePanelSizes(new Vector3(0.02f, 0f, 0.04f), new Vector3(0.025f, 0.012f, 0.001f), "small");
  }

  [MenuItem("Tools/InstaSpace/Update Panel Size to Extra Small")]
  public static void UpdatePanelSizeExtraSmall()
  {
    UpdatePanelSizes(new Vector3(0.015f, 0f, 0.03f), new Vector3(0.02f, 0.01f, 0.001f), "extra small");
  }

  private static void UpdatePanelSizes(Vector3 offset, Vector3 scale, string sizeName)
  {
    var panelBuilders = Object.FindObjectsOfType<InstaSpace.PanelUIBuilder>();
    int updatedCount = 0;

    foreach (var panel in panelBuilders)
    {
      Undo.RecordObject(panel, $"Update Panel Size to {sizeName}");
      panel.localOffset = offset;
      panel.panelScale = scale;

      // Force rebuild the panel
      panel.RebuildPanel();

      EditorUtility.SetDirty(panel);
      updatedCount++;
    }

    if (updatedCount > 0)
    {
      EditorUtility.DisplayDialog("Panel Size Update",
        $"Updated {updatedCount} PanelUIBuilder(s) to {sizeName} size.\nChanges should be visible immediately in Play mode.",
        "OK");
      Debug.Log($"[InstaSpace] Updated {updatedCount} PanelUIBuilder(s) to {sizeName} size");
    }
    else
    {
      EditorUtility.DisplayDialog("Panel Size Update",
        "No PanelUIBuilder components found in the scene.",
        "OK");
    }
  }
}