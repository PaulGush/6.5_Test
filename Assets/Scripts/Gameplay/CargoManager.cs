using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// The voyage ledger: total value delivered to the home dock, synced for the HUD.
    /// Tiny on purpose, matching the codebase's single-purpose-behaviour granularity
    /// (RunManager stays a pure stopwatch). Scene object created by the builder.
    /// </summary>
    public class CargoManager : NetworkBehaviour
    {
        [SyncVar] private int delivered;

        public int Delivered => delivered;

        [Server]
        public void ServerDeliver(int value) => delivered += value;
    }
}
