using UnityEngine;
using Meta.XR.MRUtilityKit;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Loads the MRUK room mesh and visualises it for debug purposes.
/// Compatible with Meta XR SDK v85.
///
/// SETUP:
///   1. Attach to LightingManager GameObject
///   2. Drag the MRUK component into the Mruk field
///   3. Make sure Space Setup has been run on the Quest 3
/// </summary>
public class SceneReconstructionManager : MonoBehaviour
{
    [Header("Visualisation")]
    [Tooltip("Turn off for production builds.")]
    public bool showDebugMesh = true;

    [Header("References")]
    public MRUK mruk;

    // ── Public state ──────────────────────────────────────────────────────
    public bool IsRoomLoaded { get; private set; }
    public MRUKRoom CurrentRoom { get; private set; }
    public List<Vector3> SurfaceNormals { get; private set; } = new();

    private List<GameObject> _debugObjects = new();

    // ─────────────────────────────────────────────────────────────────────
    void Start()
    {
        if (mruk == null)
            mruk = FindFirstObjectByType<MRUK>();

        if (mruk == null)
        {
            Debug.LogError("[SceneRecon] No MRUK component found. Add MRUK prefab to the scene.");
            return;
        }

        mruk.RoomCreatedEvent.AddListener(OnRoomLoaded);

        // Already loaded (e.g. editor with saved layout)
        if (MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
            OnRoomLoaded(MRUK.Instance.GetCurrentRoom());
    }

    // ─────────────────────────────────────────────────────────────────────
    void OnRoomLoaded(MRUKRoom room)
    {
        CurrentRoom = room;
        IsRoomLoaded = true;

        Debug.Log($"[SceneRecon] Room loaded with {room.Anchors.Count} anchor(s).");

        SurfaceNormals.Clear();
        CollectSurfaceNormals(room);

        if (showDebugMesh)
            StartCoroutine(VisualiseMeshes(room));
    }

    // ─────────────────────────────────────────────────────────────────────
    void CollectSurfaceNormals(MRUKRoom room)
    {
        foreach (var anchor in room.Anchors)
        {
            // In SDK v85, labels are checked via HasLabel(string)
            bool isWall    = anchor.HasLabel(OVRSceneManager.Classification.WallFace);
            bool isFloor   = anchor.HasLabel(OVRSceneManager.Classification.Floor);
            bool isCeiling = anchor.HasLabel(OVRSceneManager.Classification.Ceiling);

            if (isWall || isFloor || isCeiling)
            {
                Vector3 normal = anchor.transform.forward;
                SurfaceNormals.Add(normal);

                string label = isFloor ? "Floor" : isCeiling ? "Ceiling" : "Wall";
                Debug.Log($"[SceneRecon] {label} normal: {normal}");
            }
        }

        Debug.Log($"[SceneRecon] Collected {SurfaceNormals.Count} surface normal(s).");
    }

    // ─────────────────────────────────────────────────────────────────────
    IEnumerator VisualiseMeshes(MRUKRoom room)
    {
        foreach (var go in _debugObjects) Destroy(go);
        _debugObjects.Clear();

        yield return null; // wait a frame for mesh data to be ready

        foreach (var anchor in room.Anchors)
        {
            MeshFilter mf = anchor.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            var debugGO = new GameObject($"[Debug] {anchor.name}");
            debugGO.transform.SetParent(anchor.transform, false);

            var mr  = debugGO.AddComponent<MeshRenderer>();
            var mf2 = debugGO.AddComponent<MeshFilter>();
            mf2.sharedMesh = mf.sharedMesh;
            mr.material = MakeDebugMaterial(anchor);

            _debugObjects.Add(debugGO);
        }

        Debug.Log($"[SceneRecon] Spawned {_debugObjects.Count} debug mesh(es).");
    }

    // ─────────────────────────────────────────────────────────────────────
    Material MakeDebugMaterial(MRUKAnchor anchor)
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetFloat("_Surface", 1); // transparent

        Color c;
        if      (anchor.HasLabel(OVRSceneManager.Classification.Floor))    c = new Color(0.2f, 0.8f, 0.2f, 0.3f); // green
        else if (anchor.HasLabel(OVRSceneManager.Classification.Ceiling))  c = new Color(0.2f, 0.2f, 0.8f, 0.3f); // blue
        else if (anchor.HasLabel(OVRSceneManager.Classification.WallFace)) c = new Color(0.8f, 0.2f, 0.2f, 0.3f); // red
        else                                                                c = new Color(0.8f, 0.8f, 0.2f, 0.3f); // yellow

        mat.color = c;
        return mat;
    }

    // ─────────────────────────────────────────────────────────────────────
    public void ToggleDebugMeshes()
    {
        showDebugMesh = !showDebugMesh;
        foreach (var go in _debugObjects)
            go.SetActive(showDebugMesh);
    }

    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !IsRoomLoaded) return;
        Gizmos.color = Color.cyan;
        foreach (var n in SurfaceNormals)
            Gizmos.DrawRay(transform.position, n * 0.4f);
    }
}