using kcp2k;
using Mirror;
using Mirror.FizzySteam;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Picks the active Mirror transport before the NetworkManager wakes:
    ///  - In a standalone build: FizzySteamworks (Steam P2P).
    ///  - In the editor: KcpTransport by default. Steam is opt-in via <see cref="enableSteamInEditor"/>
    ///    (or the <c>-steam</c> flag), because in-editor SteamAPI.Init() can crash the editor on Linux.
    ///
    /// Command-line overrides: <c>-kcp</c> forces KCP, <c>-steam</c> forces FizzySteamworks.
    /// Both transport components must exist on this GameObject alongside the NetworkManager.
    /// </summary>
    [DefaultExecutionOrder(-2000)] // before NetworkManager.Awake assigns Transport.active
    [RequireComponent(typeof(NetworkManager))]
    public class TransportSelector : MonoBehaviour
    {
        [Tooltip("Use the Steam (FizzySteamworks) transport while running in the editor. OFF by " +
                 "default (editor uses KCP). Keep this in sync with the matching toggle on " +
                 "SteamManager so Steam init and transport selection agree. The -steam flag forces both on.")]
        [SerializeField] private bool enableSteamInEditor;

        private void Awake()
        {
            var manager = GetComponent<NetworkManager>();
            var kcp = GetComponent<KcpTransport>();
            var fizzy = GetComponent<FizzySteamworks>();

            // Builds default to Steam; the editor opts in via the toggle (or -steam below).
            bool useSteam = !Application.isEditor || enableSteamInEditor;

            string[] args = System.Environment.GetCommandLineArgs();
            if (System.Array.IndexOf(args, "-kcp") >= 0) useSteam = false;
            if (System.Array.IndexOf(args, "-steam") >= 0) useSteam = true;

            Transport chosen = useSteam ? (Transport)fizzy : kcp;
            if (chosen == null)
            {
                Debug.LogError("[TransportSelector] Missing " + (useSteam ? "FizzySteamworks" : "KcpTransport") + " component.");
                return;
            }

            manager.transport = chosen;
            Transport.active = chosen;
            Debug.Log("[TransportSelector] Active transport: " + chosen.GetType().Name);
        }
    }
}
