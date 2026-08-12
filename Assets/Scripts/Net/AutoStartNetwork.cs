using Mirror;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Lets a standalone build auto-start networking from command-line args, so we
    /// can run multiple local instances for replication testing without clicking the
    /// NetworkManagerHUD. The editor is left alone (driven manually).
    ///
    ///   MyGame -host
    ///   MyGame -server
    ///   MyGame -client 127.0.0.1
    ///
    /// As a client it also logs, once per second, how many player objects it sees —
    /// a simple proof that server spawns are replicating to this client.
    /// </summary>
    public class AutoStartNetwork : MonoBehaviour
    {
        private float _logTimer;

        private void Start()
        {
            if (Application.isEditor) return; // editor peer is started by hand

            string[] args = System.Environment.GetCommandLineArgs();
            NetworkManager nm = NetworkManager.singleton;
            if (nm == null)
            {
                Debug.LogError("[AutoStart] No NetworkManager.singleton in scene.");
                return;
            }

            if (System.Array.IndexOf(args, "-autowalk") >= 0)
            {
                Game.Player.NetworkPlayer.DebugAutoWalk = true;
                Debug.Log("[AutoStart] DebugAutoWalk enabled");
            }

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-host":
                        nm.StartHost();
                        Debug.Log("[AutoStart] Started HOST");
                        return;
                    case "-server":
                        nm.StartServer();
                        Debug.Log("[AutoStart] Started SERVER");
                        return;
                    case "-client":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                            nm.networkAddress = args[i + 1];
                        nm.StartClient();
                        Debug.Log("[AutoStart] Started CLIENT -> " + nm.networkAddress);
                        return;
                }
            }
        }

        private void Update()
        {
            if (Application.isEditor) return;
            if (!NetworkClient.active) return;

            _logTimer += Time.deltaTime;
            if (_logTimer < 1f) return;
            _logTimer = 0f;

            int players = 0;
            foreach (var kv in NetworkClient.spawned)
                if (kv.Value != null && kv.Value.GetComponent<Game.Player.NetworkPlayer>() != null)
                    players++;

            string myPos = NetworkClient.localPlayer != null
                ? NetworkClient.localPlayer.transform.position.ToString("F2")
                : "(none)";

            // Timeline health: snapAge is how stale the newest received server state
            // is. Flat = healthy; steadily GROWING across a session = the client's
            // interpolation timeline is falling behind (the "desyncs after 30s" case).
            double snapAge = NetworkClient.connection != null
                ? NetworkTime.time - NetworkClient.connection.remoteTimeStamp
                : -1.0;

            // Shared reference object: the lowest-netId cargo item, logged on BOTH
            // host and client. Diffing the two logs at the same wall-clock second
            // turns "the objects seem desynced" into an actual distance.
            uint cargoId = uint.MaxValue;
            Vector3 cargoPos = default;
            foreach (var kv in NetworkClient.spawned)
                if (kv.Value != null && kv.Key < cargoId
                    && kv.Value.GetComponent<Game.Gameplay.CargoItem>() != null)
                {
                    cargoId = kv.Key;
                    cargoPos = kv.Value.transform.position;
                }
            string cargo = cargoId != uint.MaxValue
                ? $" cargo{cargoId}={cargoPos.ToString("F2")}" : "";

            Debug.Log("[AutoStart] connected=" + NetworkClient.isConnected +
                      " spawnedPlayers=" + players + " localPlayerPos=" + myPos +
                      $" rtt={NetworkTime.rtt * 1000.0:F0}ms" +
                      $" snapAge={snapAge * 1000.0:F0}ms" +
                      $" buffer={NetworkClient.bufferTime * 1000.0:F0}ms" + cargo);

            // Host only: the SERVER's truth about every player's movement state, so a
            // "client was flying / stuck / sliding" report can be checked against what
            // the simulation actually thought was happening at that moment.
            if (NetworkServer.active)
                foreach (var kv in NetworkServer.spawned)
                {
                    if (kv.Value == null) continue;
                    var pc = kv.Value.GetComponent<Game.Player.PlayerController>();
                    if (pc == null || kv.Value.GetComponent<Game.Player.NetworkPlayer>() == null)
                        continue;
                    Debug.Log($"[AutoStart:srv] player{kv.Key} " +
                              $"pos={kv.Value.transform.position.ToString("F2")} " +
                              $"swim={pc.IsSwimming} climb={pc.IsClimbing} vy={pc.VerticalVelocity:F2}");
                }
        }
    }
}
