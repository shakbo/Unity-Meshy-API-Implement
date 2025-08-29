using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SpatialMeshManager:
/// - Collects vertices from a configured meshRoot (fallback) and computes floorY/ceilingY using RANSAC plane fitting.
/// - Raises event when values updated.
/// - If you have runtime XRMeshSubsystem pipeline you can extend CollectAllVertices to read from it.
/// </summary>
public class SpatialMeshManager : MonoBehaviour
{
    [Header("Sources / Fallback")]
    [Tooltip("If your scanning system parent all generated meshes under a single GameObject, set it here.")]
    public Transform meshRoot;

    [Header("RANSAC")]
    [Range(0.8f, 0.999f)]
    public float planeNormalDotThreshold = 0.96f;
    public float inlierDistanceThreshold = 0.03f;
    public int ransacIterations = 300;
    [Range(0.001f, 0.2f)]
    public float minInlierRatio = 0.01f;

    [Header("Result")]
    public float floorY = float.NaN;
    public float ceilingY = float.NaN;

    public event Action OnFloorCeilingUpdated;

    // Public API: call after scan complete or when you want recompute
    public void ComputeFloorAndCeiling()
    {
        var verts = CollectAllVertices();
        if (verts == null || verts.Count == 0)
        {
            floorY = float.NaN;
            ceilingY = float.NaN;
            OnFloorCeilingUpdated?.Invoke();
            Debug.LogWarning("SpatialMeshManager: no vertices collected.");
            return;
        }

        var floorPlane = FindDominantHorizontalPlane(verts, Vector3.up, planeNormalDotThreshold, inlierDistanceThreshold, ransacIterations, minInlierRatio);
        if (floorPlane.HasValue)
        {
            Plane p = floorPlane.Value;
            float signedDist = p.GetDistanceToPoint(Vector3.zero);
            Vector3 pointOnPlane = -p.normal * signedDist;
            floorY = pointOnPlane.y;
        }
        else
        {
            floorY = float.NaN;
        }

        var ceilingPlane = FindDominantHorizontalPlane(verts, Vector3.down, planeNormalDotThreshold, inlierDistanceThreshold, ransacIterations, minInlierRatio);
        if (ceilingPlane.HasValue)
        {
            Plane p = ceilingPlane.Value;
            float signedDist = p.GetDistanceToPoint(Vector3.zero);
            Vector3 pointOnPlane = -p.normal * signedDist;
            ceilingY = pointOnPlane.y;
        }
        else
        {
            ceilingY = float.NaN;
        }

        Debug.Log($"SpatialMeshManager: floorY={{floorY}}, ceilingY={{ceilingY}}");
        OnFloorCeilingUpdated?.Invoke();
    }

    private List<Vector3> CollectAllVertices()
    {
        var verts = new List<Vector3>();

        // Fallback: gather from MeshFilters under meshRoot. This covers typical scanner outputs that export meshes into scene.
        if (meshRoot != null)
        {
            var filters = meshRoot.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null) continue;
                var m = mf.sharedMesh;
                var localVerts = m.vertices;
                var tx = mf.transform.localToWorldMatrix;
                for (int i = 0; i < localVerts.Length; ++i)
                    verts.Add(tx.MultiplyPoint3x4(localVerts[i]));
            }
        }
        else
        {
            // Try to find objects tagged "SpatialMesh" as a convention
            try
            {
                var tagged = GameObject.FindGameObjectsWithTag("SpatialMesh");
                foreach (var go in tagged)
                {
                    var filters = go.GetComponentsInChildren<MeshFilter>(true);
                    foreach (var mf in filters)
                    {
                        if (mf.sharedMesh == null) continue;
                        var m = mf.sharedMesh;
                        var localVerts = m.vertices;
                        var tx = mf.transform.localToWorldMatrix;
                        for (int i = 0; i < localVerts.Length; ++i)
                            verts.Add(tx.MultiplyPoint3x4(localVerts[i]));
                    }
                }
            }
            catch { /* tag might not exist in project */ }
        }

        return verts;
    }

    private Plane? FindDominantHorizontalPlane(List<Vector3> points, Vector3 targetDir, float normalDotThreshold, float distThreshold, int iterations, float minInlierRatio)
    {
        if (points == null || points.Count < 30) return null;

        System.Random rnd = new System.Random();
        int bestInliers = 0;
        Plane bestPlane = new Plane();
        int N = points.Count;

        for (int it = 0; it < iterations; it++)
        {
            int i0 = rnd.Next(N), i1 = rnd.Next(N), i2 = rnd.Next(N);
            if (i0 == i1 || i1 == i2 || i0 == i2) { it--; continue; }

            Vector3 p0 = points[i0], p1 = points[i1], p2 = points[i2];
            Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
            if (normal.sqrMagnitude < 1e-8f) continue;
            normal.Normalize();

            float dot = Mathf.Abs(Vector3.Dot(normal, targetDir.normalized));
            if (dot < normalDotThreshold) continue;

            Plane candidate = new Plane(normal, p0);
            int inliers = 0;
            for (int i = 0; i < N; i++)
            {
                float d = Mathf.Abs(candidate.GetDistanceToPoint(points[i]));
                if (d <= distThreshold) inliers++;
            }

            if (inliers > bestInliers)
            {
                bestInliers = inliers;
                bestPlane = candidate;
            }
        }

        if (bestInliers < Math.Max(12, (int)(N * minInlierRatio)))
            return null;

        return bestPlane;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!float.IsNaN(floorY))
        {
            Gizmos.color = Color.green;
            Gizmos.DrawCube(new Vector3(0, floorY, 0), new Vector3(2f, 0.02f, 2f));
        }
        if (!float.IsNaN(ceilingY))
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawCube(new Vector3(0, ceilingY, 0), new Vector3(2f, 0.02f, 2f));
        }
    }
#endif
}