using UnityEngine;

public class VRWallSetup : MonoBehaviour
{
  [Header("Required Components")]
  public OVRCameraRig cameraRig;
  public Material defaultWallMaterial;

  void Start()
  {
    SetupVRWallSystem();
  }

  void SetupVRWallSystem()
  {
    // Find or create camera rig
    if (cameraRig == null)
    {
      cameraRig = FindObjectOfType<OVRCameraRig>();
    }

    // Add wall creator to scene
    GameObject wallCreator = new GameObject("WallCreator");
    VirtualWallCreator creator = wallCreator.AddComponent<VirtualWallCreator>();

    // Setup controller references
    creator.leftController = cameraRig.leftControllerAnchor.GetComponent<OVRControllerHelper>();
    creator.rightController = cameraRig.rightControllerAnchor.GetComponent<OVRControllerHelper>();
    // Also provide explicit anchor transforms so raycasts bind correctly
    creator.leftControllerAnchor = cameraRig.leftControllerAnchor;
    creator.rightControllerAnchor = cameraRig.rightControllerAnchor;
    creator.wallMaterial = defaultWallMaterial;
    // If no default transparent cutout prefab provided, create a lightweight runtime one so cutouts work out-of-the-box
    if (creator.defaultTransparentCutoutPrefab == null)
    {
      GameObject runtimeCutout = GameObject.CreatePrimitive(PrimitiveType.Cube);
      runtimeCutout.name = "_Runtime_DefaultTransparentCutout";
      // Make it a thin box (will be scaled by WallCutout.CreateTransparentCutout)
      runtimeCutout.transform.localScale = new Vector3(1f, 1f, 0.01f);
      // Create a simple transparent material
      var rend = runtimeCutout.GetComponent<Renderer>();
      if (rend != null)
      {
        Material mat = new Material(Shader.Find("Standard"));
        Color c = Color.white; c.a = 0.15f; mat.color = c; mat.SetFloat("_Mode", 3);
        // Set blending properties for Standard shader compatibility
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON"); mat.DisableKeyword("_ALPHABLEND_ON"); mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        rend.sharedMaterial = mat;
      }
      // Remove collider from the runtime prefab (we only need visuals)
      Destroy(runtimeCutout.GetComponent<Collider>());
      // Keep it inactive and hidden
      runtimeCutout.SetActive(false);
      creator.defaultTransparentCutoutPrefab = runtimeCutout;
    }

    Debug.Log("VR Wall System initialized");
    Debug.Log("Controls:");
    Debug.Log("- Right Trigger: Add wall points");
    Debug.Log("- Left Trigger: Finish wall");
    Debug.Log("- B Button: Cancel wall creation");
    Debug.Log("- X Button: Start cutout selection");
    Debug.Log("- Y Button: Create cutout");
  }
}