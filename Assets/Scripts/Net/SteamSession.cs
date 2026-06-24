using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Shared, read-mostly Steam state, written once by <see cref="SteamManager"/> and read by
    /// anyone that needs it (UI, the spawned player, the lobby).
    ///
    /// A ScriptableObject is used instead of a static singleton: it can be referenced by prefabs
    /// (e.g. the runtime-spawned player prefab, which cannot hold a scene reference), it is an
    /// explicit injected dependency rather than hidden global state, and it can be swapped for a
    /// fake asset in tests. Fields are runtime-only ([NonSerialized]) so play-mode writes never
    /// persist back into the asset.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Steam Session", fileName = "SteamSession")]
    public class SteamSession : ScriptableObject
    {
        [System.NonSerialized] public bool Initialized;
        [System.NonSerialized] public string LocalName = "Player";
    }
}
