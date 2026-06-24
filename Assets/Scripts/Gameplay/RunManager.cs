using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Server-authoritative run clock for the A→B course. Level triggers call StartRun / FinishRun;
    /// SyncVars replicate the state so any client's HUD can display the live timer, the finish time,
    /// and the best time. Uses NetworkTime so the clock is consistent across host and clients.
    /// </summary>
    public class RunManager : NetworkBehaviour
    {
        [SyncVar] private double _startTime;       // server NetworkTime at run start
        [SyncVar] private bool _running;
        [SyncVar] private bool _finished;
        [SyncVar] private double _finishedElapsed;
        [SyncVar] private double _bestElapsed = -1; // <0 = none yet

        public bool Running => _running;
        public bool Finished => _finished;
        public bool HasBest => _bestElapsed >= 0;
        public double BestElapsed => _bestElapsed;

        /// <summary>Seconds since the run started, or the final time once finished, else 0.</summary>
        public double Elapsed
        {
            get
            {
                if (_finished) return _finishedElapsed;
                if (_running) return NetworkTime.time - _startTime;
                return 0;
            }
        }

        [Server]
        public void StartRun()
        {
            if (_running || _finished) return;
            _startTime = NetworkTime.time;
            _running = true;
        }

        [Server]
        public void FinishRun()
        {
            if (!_running) return;
            _finishedElapsed = NetworkTime.time - _startTime;
            _running = false;
            _finished = true;
            if (_bestElapsed < 0 || _finishedElapsed < _bestElapsed)
                _bestElapsed = _finishedElapsed;
        }

        [Server]
        public void ResetRun()
        {
            _running = false;
            _finished = false;
        }
    }
}
