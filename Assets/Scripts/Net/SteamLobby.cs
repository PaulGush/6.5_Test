using System.Collections.Generic;
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
    /// Exposed to the UI through <see cref="ILobbyService"/> (injected as a serialized reference,
    /// not a singleton). Reads Steam state from the injected <see cref="SteamSession"/>.
    /// </summary>
    public class SteamLobby : MonoBehaviour, ILobbyService
    {
        private const string HostAddressKey = "HostAddress";

        [Tooltip("Shared Steam state (assign the SteamSession asset).")]
        [SerializeField] private SteamSession session;

        private NetworkManager _manager;

        public CSteamID CurrentLobby { get; private set; }

        private Callback<LobbyCreated_t> _lobbyCreated;
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private Callback<LobbyEnter_t> _lobbyEntered;

        private bool SteamReady => session != null && session.Initialized;

        private void Awake()
        {
            _manager = GetComponent<NetworkManager>();
        }

        private void Start()
        {
            if (!SteamReady)
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
            if (!SteamReady) { Debug.LogError("[Lobby] Steam not initialized."); return; }
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, _manager.maxConnections);
        }

        /// <summary>Open the Steam overlay invite dialog for the current lobby (overlay-only).</summary>
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

        /// <summary>True when the Steam overlay is loaded (game launched via Steam) and usable.</summary>
        public bool IsOverlayAvailable => SteamReady && SteamUtils.IsOverlayEnabled();

        /// <summary>
        /// Online Steam friends we can invite to the current lobby, sorted with same-game friends
        /// first, then alphabetically. Returns empty when Steam isn't initialized (e.g. the editor)
        /// or there is no active lobby, so callers are safe to invoke on any platform.
        /// </summary>
        public List<FriendInfo> GetInvitableFriends()
        {
            var result = new List<FriendInfo>();
            if (!SteamReady || !CurrentLobby.IsValid()) return result;

            uint appId = SteamUtils.GetAppID().m_AppId;
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendPersonaState(id) == EPersonaState.k_EPersonaStateOffline)
                    continue;

                bool inGame = SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t game)
                              && game.m_gameID.AppID().m_AppId == appId;
                result.Add(new FriendInfo(id, SteamFriends.GetFriendPersonaName(id), inGame));
            }

            result.Sort((a, b) =>
            {
                if (a.InGame != b.InGame) return a.InGame ? -1 : 1; // in-game friends first
                return string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>
        /// Send a Steam lobby invite straight to a specific friend. Unlike the overlay dialog,
        /// this needs no Steam overlay, so it works in a build launched outside Steam. The friend
        /// receives a Steam notification; accepting it fires GameLobbyJoinRequested.
        /// </summary>
        public void InviteToLobby(CSteamID friend)
        {
            if (!SteamReady) { Debug.LogWarning("[Lobby] Steam not initialized; cannot invite."); return; }
            if (!CurrentLobby.IsValid()) { Debug.LogWarning("[Lobby] No active lobby to invite to."); return; }

            bool ok = SteamMatchmaking.InviteUserToLobby(CurrentLobby, friend);
            Debug.Log("[Lobby] InviteUserToLobby(" + friend + ") -> " + ok);
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
