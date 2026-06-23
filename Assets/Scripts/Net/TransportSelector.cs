using kcp2k;
using Mirror;
using Mirror.FizzySteam;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Picks the active Mirror transport before the NetworkManager wakes:
    ///  - In the editor: KcpTransport (Steam is not initialized in-editor on Linux,
    ///    and in-editor SteamAPI.Init() can crash the editor).
    ///  - In a standalone build: FizzySteamworks (Steam P2P).
    ///
    /// Command-line overrides: <c>-kcp</c> forces KCP, <c>-steam</c> forces FizzySteamworks.
    /// Both transport components must exist on this GameObject alongside the NetworkManager.
    /// </summary>
    [DefaultExecutionOrder(-2000)] // before NetworkManager.Awake assigns Transport.active
    [RequireComponent(typeof(NetworkManager))]
    public class TransportSelector : MonoBehaviour
    {
        private void Awake()
        {
            var manager = GetComponent<NetworkManager>();
            var kcp = GetComponent<KcpTransport>();
            var fizzy = GetComponent<FizzySteamworks>();

            bool useSteam = !Application.isEditor; // builds default to Steam

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
