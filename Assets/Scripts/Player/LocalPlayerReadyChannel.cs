using System;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Event channel raised by the local player when it spawns, carrying its camera pivot.
    /// <see cref="CameraRig"/> listens and binds its cameras to that pivot.
    ///
    /// Using a ScriptableObject channel inverts the dependency: the runtime-spawned player needs
    /// no reference to the scene's CameraRig, and the CameraRig needs no reference to the player —
    /// they only share this asset. This replaces the former CameraRig singleton.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Local Player Ready Channel", fileName = "LocalPlayerReadyChannel")]
    public class LocalPlayerReadyChannel : ScriptableObject
    {
        public event Action<Transform> Raised;

        public void Raise(Transform cameraPivot) => Raised?.Invoke(cameraPivot);
    }
}
