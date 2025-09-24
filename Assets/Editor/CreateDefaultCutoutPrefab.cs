#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public class CreateDefaultCutoutPrefab
{
  [MenuItem("Tools/Create Default Transparent Cutout Prefab")]
  public static void CreatePrefab()
  {
    string prefabDir = "Assets/Prefabs";
    if (!AssetDatabase.IsValidFolder(prefabDir)) AssetDatabase.CreateFolder("Assets", "Prefabs");

    GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
    go.name = "DefaultTransparentCutout";
    go.transform.localScale = new Vector3(1f, 1f, 0.01f);
    var rend = go.GetComponent<Renderer>();
    if (rend != null)
    {
      Material mat = new Material(Shader.Find("Standard"));
      Color c = Color.white; c.a = 0.15f; mat.color = c; mat.SetFloat("_Mode", 3);
      mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
      mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
      mat.SetInt("_ZWrite", 0);
      mat.DisableKeyword("_ALPHATEST_ON"); mat.DisableKeyword("_ALPHABLEND_ON"); mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
      mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
      rend.sharedMaterial = mat;
    }
    // remove collider
    var col = go.GetComponent<Collider>(); if (col != null) Object.DestroyImmediate(col);

    string path = prefabDir + "/DefaultTransparentCutout.prefab";
    Object prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
    if (prefab != null) Debug.Log("Created prefab at " + path);
    else Debug.LogWarning("Failed to create prefab at " + path);

    // Try to assign to VRWallSetup instances in scene
    var setups = Object.FindObjectsOfType<VRWallSetup>();
    foreach (var s in setups)
    {
      var so = new SerializedObject(s);
      var prop = so.FindProperty("defaultWallMaterial");
      // We cannot assign prefab directly via SerializedObject here reliably; instead assign by reference
      s.GetType().GetField("defaultWallMaterial")?.SetValue(s, s.defaultWallMaterial);
    }

    Object.DestroyImmediate(go);
    AssetDatabase.SaveAssets();
    AssetDatabase.Refresh();
  }
}
#endif