using UnityEngine;
#if !UNITY_EDITOR
using Steamworks;
#endif

namespace Game.Net
{
    /// <summary>
    /// Owns the Steamworks lifecycle in standalone builds: initializes the Steam API,
    /// pumps callbacks each frame, and shuts down on quit. Survives scene loads.
    ///
    /// IMPORTANT: Steam is initialized in BUILDS ONLY. On Linux, calling
    /// SteamAPI.Init() inside the Unity Editor can hard-hang/crash the editor, so the
    /// init path is excluded from editor compilation entirely via #if !UNITY_EDITOR.
    /// Test Steam networking with standalone builds; the editor uses KCP.
    ///
    /// Requires the Steam client running and logged in, plus a steam_appid.txt
    /// (480 = Spacewar for dev) next to the executable.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class SteamManager : MonoBehaviour
    {
        public static SteamManager Instance { get; private set; }
        public static bool Initialized { get; private set; }

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

#if UNITY_EDITOR
            Debug.Log("[Steam] Editor: SteamAPI is not initialized (builds only). Editor networking uses KCP.");
#else
            try
            {
                Initialized = SteamAPI.Init();
            }
            catch (System.DllNotFoundException e)
            {
                Debug.LogError("[Steam] Native Steamworks library not found: " + e.Message);
                Initialized = false;
                return;
            }

            if (!Initialized)
                Debug.LogError("[Steam] SteamAPI.Init() failed. Is the Steam client running and logged in, " +
                               "and is steam_appid.txt present?");
            else
                Debug.Log("[Steam] Initialized. Logged in as: " + SteamFriends.GetPersonaName() +
                          " (" + SteamUser.GetSteamID() + ")");
#endif
        }

#if !UNITY_EDITOR
        private void Update()
        {
            if (Initialized) SteamAPI.RunCallbacks();
        }

        private void OnApplicationQuit() => ShutdownSteam();
        private void OnDestroy()
        {
            if (Instance == this) ShutdownSteam();
        }

        private void ShutdownSteam()
        {
            if (Instance != this) return;
            Instance = null;
            if (Initialized)
            {
                SteamAPI.Shutdown();
                Initialized = false;
            }
        }
#endif
    }
}
