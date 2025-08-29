using UnityEngine;

/// <summary>
/// MeshSlicer: lightweight helper that provides an interface for creating rectangular holes on wall meshes.
/// Note: This implementation does not perform robust boolean mesh operations. It creates a child quad with a hole material
/// and marks it as "cut" for simple visibility or decal workflows. For production, integrate a proper CSG/boolean library.
/// </summary>
public class MeshSlicer : MonoBehaviour
{
    [System.Serializable]
    public class HoleCutter : MonoBehaviour
    {
        public Vector2 size = new Vector2(0.5f, 1f);
    }

    public Material holeMaterial;

    void Start()
    {
        // Find cutters under this object
        var cutters = GetComponentsInChildren<HoleCutter>();
        foreach (var c in cutters)
        {
            CreateHoleVisual(c);
        }
    }

    private void CreateHoleVisual(HoleCutter cutter)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "HoleVisual";
        quad.transform.SetParent(cutter.transform, false);
        quad.transform.localScale = new Vector3(cutter.size.x, cutter.size.y, 1f);
        if (holeMaterial != null) quad.GetComponent<MeshRenderer>().sharedMaterial = holeMaterial;
        // Flip normal so it looks like a hole when placed on wall
        quad.transform.localRotation = Quaternion.Euler(0, 180, 0);
        // Make sure collider does not block
        DestroyImmediate(quad.GetComponent<Collider>());
    }
}