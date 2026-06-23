using Mirror;
using Steamworks;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Steam lobby flow for small co-op over FizzySteamworks (Steam P2P):
    ///  - Host: create a Steam lobby, stash the host's SteamID in lobby data, StartHost.
    ///  - Invitee: accepting an invite / "Join Game" fires GameLobbyJoinRequested ->
    ///    join the lobby -> read host SteamID -> set it as the transport address -> StartClient.
    ///
    /// Requires <see cref="SteamManager"/> initialized and the NetworkManager's active
    /// transport to be FizzySteamworks (so networkAddress is a SteamID, not an IP).
    /// </summary>
    public class SteamLobby : MonoBehaviour
    {
        public static SteamLobby Instance { get; private set; }

        private const string HostAddressKey = "HostAddress";

        private NetworkManager _manager;

        public CSteamID CurrentLobby { get; private set; }

        private Callback<LobbyCreated_t> _lobbyCreated;
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private Callback<LobbyEnter_t> _lobbyEntered;

        private void Awake()
        {
            Instance = this;
            _manager = GetComponent<NetworkManager>();
        }

        private void Start()
        {
            if (!SteamManager.Initialized)
            {
                Debug.LogWarning("[Lobby] Steam not initialized; lobby features disabled.");
                return;
            }

            _lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);

            // Test hook: a build started with -hostlobby auto-creates a lobby and hosts.
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-hostlobby") >= 0)
                HostLobby();
        }

        /// <summary>Create a friends-only lobby and host once it exists.</summary>
        public void HostLobby()
        {
            if (!SteamManager.Initialized) { Debug.LogError("[Lobby] Steam not initialized."); return; }
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, _manager.maxConnections);
        }

        /// <summary>Open the Steam overlay invite dialog for the current lobby.</summary>
        public void InviteFriends()
        {
            if (CurrentLobby.IsValid())
                SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobby);
            else
                Debug.LogWarning("[Lobby] No active lobby to invite to.");
        }

        public void LeaveLobby()
        {
            if (!CurrentLobby.IsValid()) return;
            SteamMatchmaking.LeaveLobby(CurrentLobby);
            CurrentLobby = CSteamID.Nil;
        }

        private void OnLobbyCreated(LobbyCreated_t cb)
        {
            if (cb.m_eResult != EResult.k_EResultOK)
            {
                Debug.LogError("[Lobby] CreateLobby failed: " + cb.m_eResult);
                return;
            }

            CurrentLobby = new CSteamID(cb.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(CurrentLobby, HostAddressKey, SteamUser.GetSteamID().ToString());
            _manager.StartHost();
            Debug.Log("[Lobby] Created and hosting. Lobby=" + CurrentLobby);
        }

        // Fired when the user accepts an invite or clicks "Join Game" on a friend.
        private void OnJoinRequested(GameLobbyJoinRequested_t cb)
        {
            SteamMatchmaking.JoinLobby(cb.m_steamIDLobby);
        }

        private void OnLobbyEntered(LobbyEnter_t cb)
        {
            CurrentLobby = new CSteamID(cb.m_ulSteamIDLobby);

            // The host enters its own lobby too, but it is already serving.
            if (NetworkServer.active) return;

            string hostAddress = SteamMatchmaking.GetLobbyData(CurrentLobby, HostAddressKey);
            if (string.IsNullOrEmpty(hostAddress))
            {
                Debug.LogError("[Lobby] No host address in lobby data.");
                return;
            }

            _manager.networkAddress = hostAddress;
            _manager.StartClient();
            Debug.Log("[Lobby] Entered lobby; connecting to host SteamID " + hostAddress);
        }
    }
}
