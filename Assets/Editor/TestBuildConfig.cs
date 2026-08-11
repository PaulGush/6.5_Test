using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Editor-only publish settings for the friend-test pipeline, kept as an asset so
    /// targets live in data instead of code. Auto-created at
    /// Assets/Editor/TestBuildConfig.asset the first time the pipeline needs it —
    /// fill the fields in the Inspector.
    /// </summary>
    public class TestBuildConfig : ScriptableObject
    {
        [Tooltip("itch.io push target as \"username/game-slug\" (create the project on " +
                 "itch.io first, visibility Restricted). butler pushes the win64/linux64 " +
                 "channels there. Empty = the push menu explains setup.")]
        public string itchTarget = "";
    }
}
