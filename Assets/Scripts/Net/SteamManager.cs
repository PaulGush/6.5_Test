using UnityEngine;
using Steamworks;

namespace Game.Net
{
    /// <summary>
    /// Owns the Steamworks lifecycle: initializes the Steam API, pumps callbacks each frame, and
    /// shuts down on quit. Writes the result into the injected <see cref="SteamSession"/> so other
    /// systems read shared Steam state from there instead of a global singleton.
    ///
    /// Standalone builds always initialize Steam. In the Unity Editor, init is OPT-IN via
    /// <see cref="enableSteamInEditor"/> (or the <c>-steam</c> command-line flag) and OFF by
    /// default, because on Linux calling SteamAPI.Init() inside the editor can hard-hang/crash it.
    /// When off, the editor leaves Steam uninitialized and networking falls back to KCP.
    ///
    /// Requires the Steam client running and logged in, plus a steam_appid.txt (480 = Spacewar for
    /// dev) next to the executable (project root in-editor).
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class SteamManager : MonoBehaviour
    {
        [Tooltip("Shared Steam state this manager populates (assign the SteamSession asset).")]
        [SerializeField] private SteamSession session;

        [Tooltip("Initialize the Steam API while running in the editor. OFF by default: on Linux, " +
                 "SteamAPI.Init() inside the editor can hang/crash it. Turn on (or pass -steam) only " +
                 "when you want to test the real Steam path in-editor with the Steam client running. " +
                 "Keep this in sync with the matching toggle on TransportSelector.")]
        [SerializeField] private bool enableSteamInEditor;

        // Process-level guard against a second SteamAPI.Init (e.g. on a scene reload). This is an
        // internal init latch, not a service-access singleton.
        private static bool _apiStarted;

        // Builds always init; the editor only inits when explicitly opted in (toggle or -steam flag).
        private bool ShouldInitSteam()
        {
            if (!Application.isEditor) return true;
            if (enableSteamInEditor) return true;
            return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-steam") >= 0;
        }

        private void Awake()
        {
            Application.runInBackground = true; // a host must keep simulating while unfocused
            DontDestroyOnLoad(gameObject);

            if (session == null)
            {
                Debug.LogError("[Steam] No SteamSession assigned to SteamManager.");
                return;
            }

            if (!ShouldInitSteam())
            {
                session.Initialized = false;
                session.LocalName = "EditorPlayer";
                Debug.Log("[Steam] Editor: SteamAPI not initialized (enableSteamInEditor is off). " +
                          "Editor networking uses KCP.");
                return;
            }

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
        }

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
    }
}
