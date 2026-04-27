using UnityEngine;
using UnityEngine.SceneManagement;

// Automatically requests camera and scene permissions on first scene load.
// No GameObject attachment needed — [RuntimeInitializeOnLoadMethod] fires it automatically.
internal static class RequestPermissionsOnce
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AfterSceneLoad()
    {
        bool requested = false;
        SceneManager.sceneLoaded += (scene, _) =>
        {
            if (!requested)
            {
                requested = true;
                OVRPermissionsRequester.Request(new[]
                {
                    OVRPermissionsRequester.Permission.Scene,
                    OVRPermissionsRequester.Permission.PassthroughCameraAccess
                });
            }
        };
    }
}
