using UnityEngine;

// Automatically requests camera and scene permissions on first scene load.
// No GameObject attachment needed — [RuntimeInitializeOnLoadMethod] fires it automatically.
internal static class RequestPermissionsOnce
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AfterSceneLoad()
    {
#if !UNITY_EDITOR && UNITY_ANDROID
        // Request directly. Subscribing to SceneManager.sceneLoaded here would never
        // fire for the first scene — that event has already been dispatched by the
        // time AfterSceneLoad callbacks run, so the prompt never appeared.
        Debug.Log("[Permissions] Requesting Scene + PassthroughCameraAccess…");
        OVRPermissionsRequester.Request(new[]
        {
            OVRPermissionsRequester.Permission.Scene,
            OVRPermissionsRequester.Permission.PassthroughCameraAccess
        });
#endif
    }
}
