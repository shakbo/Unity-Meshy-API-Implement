using UnityEngine;

/// <summary>
/// Simple placement tool:
/// - Shows a preview prefab at raycast hit from camera center / pointer
/// - On confirm, instantiates the final prefab
/// </summary>
public class PlacementTool : MonoBehaviour
{
    public Camera previewCamera;
    public GameObject previewPrefab;
    public GameObject finalPrefab;

    private GameObject previewInstance;

    void Update()
    {
        if (previewCamera == null || previewPrefab == null) return;

        Ray r = previewCamera.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
        if (Physics.Raycast(r, out RaycastHit hit, 10f))
        {
            if (previewInstance == null) previewInstance = Instantiate(previewPrefab);
            previewInstance.transform.position = hit.point;
            previewInstance.transform.rotation = Quaternion.LookRotation(hit.normal);
        }
        else
        {
            if (previewInstance != null) Destroy(previewInstance);
        }

        if (Input.GetMouseButtonDown(0) && previewInstance != null)
        {
            Instantiate(finalPrefab, previewInstance.transform.position, previewInstance.transform.rotation);
        }
    }
}