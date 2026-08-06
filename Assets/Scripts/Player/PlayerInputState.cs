namespace Game.Player
{
    /// <summary>
    /// One frame of player intent, fully decoupled from the input backend.
    /// Phase 1 fills this locally; in Phase 3 this same struct is what an
    /// owning client serializes and sends to the server each tick.
    /// </summary>
    public struct PlayerInputState
    {
        public UnityEngine.Vector2 Move;   // x = strafe, y = forward
        public UnityEngine.Vector2 Look;   // mouse/stick delta (already frame-accumulated)
        public bool Sprint;
        public bool Crouch;                 // held while the crouch button is down
        public bool JumpPressed;            // edge-triggered: true only on the sample the press occurred
        public bool FreeLook;               // third person: look orbits the camera; the body is steered by Move
    }
}
