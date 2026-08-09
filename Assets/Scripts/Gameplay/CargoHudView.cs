using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Gameplay
{
    /// <summary>
    /// Top-right cargo readout: what's lashed in the ship's hold and what the crew has
    /// delivered. Self-bootstrapping programmatic uGUI (same pattern as the health HUD),
    /// polling the synced CargoHold / CargoManager each frame like RunHudView does.
    /// </summary>
    public class CargoHudView : MonoBehaviour
    {
        private Text _text;
        private Game.Ship.CargoHold _hold;
        private CargoManager _manager;
        private float _nextScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<CargoHudView>() != null) return;
            var go = new GameObject("CargoHudView");
            go.AddComponent<CargoHudView>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var textGo = new GameObject("Cargo", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(canvasGo.transform, false);
            var rect = (RectTransform)textGo.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-24f, -18f);
            rect.sizeDelta = new Vector2(560f, 60f);
            _text = textGo.GetComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.fontSize = 26;
            _text.alignment = TextAnchor.UpperRight;
            _text.color = new Color(0.92f, 0.88f, 0.78f);
            _text.raycastTarget = false;

            var shadow = textGo.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
        }

        private void Update()
        {
            bool playing = NetworkClient.active && NetworkClient.localPlayer != null;
            if (!playing)
            {
                _text.text = "";
                return;
            }

            if ((_hold == null || _manager == null) && Time.time >= _nextScan)
            {
                _nextScan = Time.time + 1f;
                if (_hold == null) _hold = FindAnyObjectByType<Game.Ship.CargoHold>();
                if (_manager == null) _manager = FindAnyObjectByType<CargoManager>();
            }

            string stowed = _hold != null
                ? $"Cargo in hold  {_hold.StowedCount}  ({_hold.StowedValue}g)"
                : "";
            string delivered = _manager != null && _manager.Delivered > 0
                ? $"Delivered  {_manager.Delivered}g"
                : "";
            _text.text = string.IsNullOrEmpty(delivered) ? stowed
                : string.IsNullOrEmpty(stowed) ? delivered
                : stowed + "\n" + delivered;
        }
    }
}
