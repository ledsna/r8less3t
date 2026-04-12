using UnityEngine;

/// <summary>
/// Assigns a unique Object ID to this Renderer via SetShaderUserValue().
/// The value is stored in unity_RenderingLayer.y (UnityPerDraw CBUFFER),
/// accessible in shaders as unity_RendererUserValue. SRP Batcher safe.
///
/// The shader combines this with the per-material _SubmeshID to produce
/// a float2 Object ID written to _CameraObjectIDTexture:
///   .x = unity_RendererUserValue  (per-object, from this script)
///   .y = _SubmeshID               (per-material, set in the Inspector)
///
/// Override mode: Set useOverrideID = true and assign the same overrideID
/// to multiple objects to make them share an Object ID (e.g. for grouping).
/// Auto-assigned IDs start at 1000, so manual overrides in the 0-999 range
/// will never collide with auto-assigned ones.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class WriteRendererID : MonoBehaviour
{
    [Tooltip("Enable to manually set a shared Object ID (0-999). " +
             "Multiple objects with the same override ID will be treated as one object.")]
    [SerializeField] private bool useOverrideID;

    [Tooltip("Manual Object ID. Use the same value on multiple objects to group them. " +
             "Keep in the 0-999 range to avoid collisions with auto-assigned IDs.")]
    [SerializeField] private uint overrideID;

    // Auto-assigned IDs start well above the manual override range.
    static uint s_Counter = 999;

    /// <summary>
    /// Returns the next auto-assigned ID. Used by GrassHolder and other
    /// scripts that instance meshes programmatically without a Renderer.
    /// </summary>
    public static uint GetNextID() => ++s_Counter;

    void OnEnable()
    {
        uint id;

        if (useOverrideID)
        {
            id = overrideID;
        }
        else
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // In Edit Mode, use the object's unique Unity Instance ID
                // (Cast unchecked to uint because GetInstanceID can be negative)
                id = unchecked((uint)gameObject.GetInstanceID());
            }
            else
#endif
            {
                // In Play Mode, use the clean incremental counter
                id = ++s_Counter;
            }
        }

        // SetShaderUserValue is defined independently on MeshRenderer and
        // SkinnedMeshRenderer, not on the Renderer base class.
        if (TryGetComponent<MeshRenderer>(out var mr))
            mr.SetShaderUserValue(id);
        else if (TryGetComponent<SkinnedMeshRenderer>(out var smr))
            smr.SetShaderUserValue(id);
    }

    void OnDisable()
    {
        uint id = 0; // 0 means "background" or "no ID"
        if (TryGetComponent<MeshRenderer>(out var mr))
            mr.SetShaderUserValue(id);
        else if (TryGetComponent<SkinnedMeshRenderer>(out var smr))
            smr.SetShaderUserValue(id);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (isActiveAndEnabled)
        {
            OnEnable();
        }
    }
#endif

    //     void Update()
    //     {
    // #if UNITY_EDITOR
    //         // In the editor, if you change the transform or hierarchy, we want to ensure
    //         // it updates immediately if it hasn't fired OnEnable
    //         if (!Application.isPlaying && transform.hasChanged)
    //         {
    //             OnEnable();
    //             transform.hasChanged = false;
    //         }
    // #endif
    //     }
}
