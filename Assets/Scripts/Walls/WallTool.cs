using UnityEngine;

/// <summary>
/// WallTool: Creates a simple box mesh representing a wall given width, height and thickness.
/// Also provides a helper to add a rectangular hole by spawning a placeholder (MeshSlicer will try to perform a cut).
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class WallTool : MonoBehaviour
{
    public float width = 2f;
    public float height = 2.5f;
    public float thickness = 0.1f;

    private MeshFilter mf;

    void Awake()
    {
        mf = GetComponent<MeshFilter>();
        BuildWall();
    }

    public void BuildWall()
    {
        Mesh m = new Mesh();
        Vector3 hw = new Vector3(width * 0.5f, height * 0.5f, thickness * 0.5f);

        Vector3[] v = new Vector3[8]
        {
            new Vector3(-hw.x,-hw.y,-hw.z),
            new Vector3(hw.x,-hw.y,-hw.z),
            new Vector3(hw.x,hw.y,-hw.z),
            new Vector3(-hw.x,hw.y,-hw.z),

            new Vector3(-hw.x,-hw.y,hw.z),
            new Vector3(hw.x,-hw.y,hw.z),
            new Vector3(hw.x,hw.y,hw.z),
            new Vector3(-hw.x,hw.y,hw.z)
        };
        int[] tris = new int[]
        {
            // front
            0,2,1, 0,3,2,
            // back
            4,5,6, 4,6,7,
            // left
            0,4,7, 0,7,3,
            // right
            1,2,6, 1,6,5,
            // top
            3,7,6, 3,6,2,
            // bottom
            0,1,5, 0,5,4
        };

        m.vertices = v;
        m.triangles = tris;
        m.RecalculateNormals();
        mf.sharedMesh = m;
    }

    public void AddRectangularHole(Vector2 center, Vector2 size)
    {
        // Spawn a cutter placeholder to be handled by MeshSlicer
        var cutter = new GameObject("HoleCutter");
        cutter.transform.SetParent(transform, false);
        cutter.transform.localPosition = new Vector3(center.x, center.y - height * 0.5f, 0);
        var hs = cutter.AddComponent<MeshSlicer.HoleCutter>();
        hs.size = size;
    }
}