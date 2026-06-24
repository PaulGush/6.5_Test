using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Net
{
    /// <summary>
    /// Controller for the in-game menu (replaces Mirror's dev NetworkManagerHUD). All UI is
    /// authored in the MenuRoot prefab and supplied here as serialized references — this script
    /// only drives behaviour and data, it never builds or searches for UI.
    ///
    ///  - "Host" starts a session. In a Steam build it creates a Steam lobby
    ///    (<see cref="SteamLobby.HostLobby"/>); in the editor (KCP, no Steam) it StartHost()s.
    ///  - "Invite Friends" opens the native Steam overlay invite dialog when the overlay is
    ///    available (game launched via Steam), otherwise it opens an in-game friend picker that
    ///    fills itself from <see cref="SteamLobby.GetInvitableFriends"/> using FriendRow prefabs.
    ///  - "Leave" stops the host/client.
    ///
    /// Joining is driven by Steam (accepting an invite), handled by <see cref="SteamLobby"/>,
    /// so there is deliberately no manual Join button.
    /// </summary>
    public class MainMenuUI : MonoBehaviour
    {
        [Header("Buttons")]
        [SerializeField] private Button hostButton;
        [SerializeField] private Button inviteButton;
        [SerializeField] private Button leaveButton;
        [SerializeField] private Button friendsCloseButton;

        [Header("Status")]
        [SerializeField] private Text statusText;

        [Header("Friends panel")]
        [SerializeField] private GameObject friendsPanel;
        [SerializeField] private Text friendsTitle;
        [SerializeField] private RectTransform friendsContent;
        [SerializeField] private Text friendsEmptyLabel;
        [SerializeField] private FriendRowView friendRowPrefab;

        [Header("Services (injected)")]
        [Tooltip("Lobby service — assign the SteamLobby in the scene.")]
        [SerializeField] private SteamLobby lobby;
        [Tooltip("Shared Steam state — assign the SteamSession asset.")]
        [SerializeField] private SteamSession session;

        [Header("Roots (disabled on headless/-batchmode builds)")]
        [SerializeField] private GameObject canvasRoot;
        [SerializeField] private GameObject eventSystemRoot;

        private NetworkManager _manager;
        private bool _friendsPanelOpen;

        // Depend on the abstraction for behaviour; the serialized field is concrete only because
        // Unity can't serialize an interface reference.
        private ILobbyService Lobby => lobby;
        private bool SteamInitialized => session != null && session.Initialized;
        private string LocalName => session != null ? session.LocalName : "Player";

        private void Start()
        {
            _manager = NetworkManager.singleton;

            // Headless/dedicated builds have no use for the menu or an EventSystem.
            if (Application.isBatchMode)
            {
                if (canvasRoot != null) canvasRoot.SetActive(false);
                if (eventSystemRoot != null) eventSystemRoot.SetActive(false);
                enabled = false;
                return;
            }

            hostButton.onClick.AddListener(OnHostClicked);
            inviteButton.onClick.AddListener(OnInviteClicked);
            leaveButton.onClick.AddListener(OnLeaveClicked);
            if (friendsCloseButton != null)
                friendsCloseButton.onClick.AddListener(() => SetFriendsPanel(false));

            SetFriendsPanel(false);
        }

        private void Update()
        {
            bool serving = NetworkServer.active;
            bool connected = serving || NetworkClient.active;

            hostButton.gameObject.SetActive(!connected);
            leaveButton.gameObject.SetActive(connected);

            // Inviting only makes sense for a host in a real Steam session.
            bool canInvite = serving && SteamInitialized;
            inviteButton.gameObject.SetActive(canInvite);
            if (!canInvite && _friendsPanelOpen)
                SetFriendsPanel(false);

            statusText.text = BuildStatus(serving, connected);
        }

        private string BuildStatus(bool serving, bool connected)
        {
            string who = LocalName;
            if (!connected)
                return SteamInitialized
                    ? who + " — not connected.\nHost, or accept a Steam invite to join."
                    : who + " — not connected (editor/KCP).\nClick Host to start.";
            if (serving && NetworkClient.active)
                return who + " — Hosting (" + NetworkServer.connections.Count + " connected).";
            if (serving)
                return who + " — Server running.";
            return who + " — Connected to host.";
        }

        // ---- button handlers ---------------------------------------------------

        private void OnHostClicked()
        {
            if (Lobby != null && SteamInitialized)
                Lobby.HostLobby();        // Steam build: create lobby, then StartHost
            else
                _manager.StartHost();     // editor/KCP: host directly
        }

        private void OnInviteClicked()
        {
            // Prefer the native Steam overlay invite dialog when it's available (game launched via
            // Steam). Otherwise fall back to the in-game friend picker.
            if (Lobby != null && Lobby.IsOverlayAvailable)
            {
                Lobby.InviteFriends();
                return;
            }
            SetFriendsPanel(!_friendsPanelOpen);
        }

        private void OnLeaveClicked()
        {
            if (NetworkServer.active && NetworkClient.isConnected) _manager.StopHost();
            else if (NetworkClient.isConnected) _manager.StopClient();
            else if (NetworkServer.active) _manager.StopServer();
        }

        // ---- friend picker -----------------------------------------------------

        private void SetFriendsPanel(bool open)
        {
            _friendsPanelOpen = open;
            if (friendsPanel != null)
                friendsPanel.SetActive(open);
            if (open)
                PopulateFriends();
        }

        private void PopulateFriends()
        {
            // Clear previously instantiated rows.
            for (int i = friendsContent.childCount - 1; i >= 0; i--)
                Destroy(friendsContent.GetChild(i).gameObject);

            List<FriendInfo> friends = Lobby != null
                ? Lobby.GetInvitableFriends()
                : new List<FriendInfo>();

            friendsTitle.text = "Invite a friend (" + friends.Count + ")";

            if (friendsEmptyLabel != null)
            {
                friendsEmptyLabel.gameObject.SetActive(friends.Count == 0);
                friendsEmptyLabel.text = SteamInitialized
                    ? "No online friends found."
                    : "Steam only (run a build).";
            }

            foreach (var info in friends)
            {
                var f = info; // per-iteration local: captured by the closure below
                FriendRowView row = Instantiate(friendRowPrefab, friendsContent);
                row.Bind(f.Name, f.InGame, () =>
                {
                    Lobby?.InviteToLobby(f.Id);
                    if (statusText != null) statusText.text = LocalName + " — invited " + f.Name + ".";
                });
            }
        }
    }
}
