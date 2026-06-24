using System.Collections.Generic;
using Steamworks;

namespace Game.Net
{
    /// <summary>A friend that can be invited, with a display name and whether they're in this game.</summary>
    public readonly struct FriendInfo
    {
        public readonly CSteamID Id;
        public readonly string Name;
        public readonly bool InGame; // playing the same app (most relevant to invite)
        public FriendInfo(CSteamID id, string name, bool inGame) { Id = id; Name = name; InGame = inGame; }
    }

    /// <summary>
    /// Lobby/invite operations, implemented by <see cref="SteamLobby"/>. Consumers depend on this
    /// abstraction and receive it via injection (a serialized reference) rather than a global
    /// singleton, so the dependency is explicit and substitutable.
    /// </summary>
    public interface ILobbyService
    {
        bool IsOverlayAvailable { get; }
        void HostLobby();
        void InviteFriends();
        void InviteToLobby(CSteamID friend);
        List<FriendInfo> GetInvitableFriends();
    }
}
