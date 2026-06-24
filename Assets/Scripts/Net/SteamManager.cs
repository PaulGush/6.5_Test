using UnityEngine;
#if !UNITY_EDITOR
using Steamworks;
#endif

namespace Game.Net
{
    /// <summary>
    /// Owns the Steamworks lifecycle in standalone builds: initializes the Steam API, pumps
    /// callbacks each frame, and shuts down on quit. Writes the result into the injected
    /// <see cref="SteamSession"/> so other systems read shared Steam state from there instead of a
    /// global singleton.
    ///
    /// IMPORTANT: Steam is initialized in BUILDS ONLY. On Linux, calling SteamAPI.Init() inside the
    /// Unity Editor can hard-hang/crash the editor, so the init path is excluded from editor
    /// compilation entirely via #if !UNITY_EDITOR. The editor uses KCP networking.
    ///
    /// Requires the Steam client running and logged in, plus a steam_appid.txt (480 = Spacewar for
    /// dev) next to the executable.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class SteamManager : MonoBehaviour
    {
        [Tooltip("Shared Steam state this manager populates (assign the SteamSession asset).")]
        [SerializeField] private SteamSession session;

        // Process-level guard against a second SteamAPI.Init (e.g. on a scene reload). This is an
        // internal init latch, not a service-access singleton.
        private static bool _apiStarted;

        private void Awake()
        {
            Application.runInBackground = true; // a host must keep simulating while unfocused
            DontDestroyOnLoad(gameObject);

            if (session == null)
            {
                Debug.LogError("[Steam] No SteamSession assigned to SteamManager.");
                return;
            }

#if UNITY_EDITOR
            session.Initialized = false;
            session.LocalName = "EditorPlayer";
            Debug.Log("[Steam] Editor: SteamAPI is not initialized (builds only). Editor networking uses KCP.");
#else
            if (_apiStarted) return;

            try
            {
                session.Initialized = SteamAPI.Init();
                _apiStarted = session.Initialized;
            }
            catch (System.DllNotFoundException e)
            {
                Debug.LogError("[Steam] Native Steamworks library not found: " + e.Message);
                session.Initialized = false;
                return;
            }

            if (!session.Initialized)
                Debug.LogError("[Steam] SteamAPI.Init() failed. Is the Steam client running and logged in, " +
                               "and is steam_appid.txt present?");
            else
            {
                session.LocalName = SteamFriends.GetPersonaName();
                Debug.Log("[Steam] Initialized. Logged in as: " + session.LocalName +
                          " (" + SteamUser.GetSteamID() + ")");
            }
#endif
        }

#if !UNITY_EDITOR
        private void Update()
        {
            if (session != null && session.Initialized) SteamAPI.RunCallbacks();
        }

        private void OnApplicationQuit() => ShutdownSteam();
        private void OnDestroy() => ShutdownSteam();

        private void ShutdownSteam()
        {
            if (session == null || !session.Initialized) return;
            SteamAPI.Shutdown();
            session.Initialized = false;
            _apiStarted = false;
        }
#endif
    }
}
