using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.Player
{
    /// <summary>
    /// Client-side health UI for the local player, built programmatically (no prefab or
    /// scene object to maintain — it self-bootstraps like UnderwaterView):
    ///  - a health bar bottom-left while in a session,
    ///  - a red vignette flash whenever health drops,
    ///  - a death screen (fade to black, cause of death, Respawn button / Space) that
    ///    frees the cursor via <see cref="CursorLock.ForceUnlock"/> and asks the server
    ///    to revive via <see cref="NetworkPlayer.RequestRespawn"/>.
    /// All state is read off the local <see cref="NetworkPlayer"/>'s SyncVars each frame.
    /// </summary>
    public class PlayerHealthHud : MonoBehaviour
    {
        private const float BarWidth = 320f, BarHeight = 18f, BarPad = 2f;

        private RectTransform _barFill;
        private Image _barFillImage;
        private GameObject _barRoot;
        private Image _flash;
        private CanvasGroup _death;
        private Text _causeText;
        private float _flashAlpha;
        private float _prevHealth = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<PlayerHealthHud>() != null) return;
            var go = new GameObject("PlayerHealthHud");
            go.AddComponent<PlayerHealthHud>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50; // above the run HUD, below nothing that matters
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Damage flash: full-screen, never intercepts the mouse.
            _flash = NewImage(canvasGo.transform, "Flash", new Color(0.8f, 0.05f, 0.05f, 0f));
            Stretch(_flash.rectTransform);
            _flash.raycastTarget = false;

            // Health bar, bottom-left.
            _barRoot = new GameObject("HealthBar");
            _barRoot.transform.SetParent(canvasGo.transform, false);
            var barRect = _barRoot.AddComponent<RectTransform>();
            barRect.anchorMin = barRect.anchorMax = barRect.pivot = Vector2.zero;
            barRect.anchoredPosition = new Vector2(24f, 24f);
            barRect.sizeDelta = new Vector2(BarWidth, BarHeight);
            var barBg = _barRoot.AddComponent<Image>();
            barBg.color = new Color(0f, 0f, 0f, 0.55f);
            barBg.raycastTarget = false;

            var fill = NewImage(_barRoot.transform, "Fill", new Color(0.3f, 0.85f, 0.35f, 0.95f));
            fill.raycastTarget = false;
            _barFill = fill.rectTransform;
            _barFillImage = fill;
            _barFill.anchorMin = _barFill.anchorMax = _barFill.pivot = new Vector2(0f, 0.5f);
            _barFill.anchoredPosition = new Vector2(BarPad, 0f);
            _barFill.sizeDelta = new Vector2(BarWidth - 2f * BarPad, BarHeight - 2f * BarPad);

            // Death screen overlay.
            var deathGo = new GameObject("DeathScreen", typeof(RectTransform), typeof(CanvasGroup));
            deathGo.transform.SetParent(canvasGo.transform, false);
            Stretch((RectTransform)deathGo.transform);
            _death = deathGo.GetComponent<CanvasGroup>();
            _death.alpha = 0f;
            _death.blocksRaycasts = false;
            _death.interactable = false;

            var blackout = NewImage(deathGo.transform, "Blackout", new Color(0.02f, 0.02f, 0.03f, 0.88f));
            Stretch(blackout.rectTransform);

            Text title = NewText(deathGo.transform, "Title", "YOU DIED", font, 72,
                new Color(0.78f, 0.12f, 0.12f));
            Place(title.rectTransform, new Vector2(0.5f, 0.62f), new Vector2(900f, 90f));

            _causeText = NewText(deathGo.transform, "Cause", "", font, 28,
                new Color(0.85f, 0.82f, 0.78f));
            Place(_causeText.rectTransform, new Vector2(0.5f, 0.52f), new Vector2(900f, 40f));

            var buttonGo = new GameObject("RespawnButton", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonGo.transform.SetParent(deathGo.transform, false);
            Place((RectTransform)buttonGo.transform, new Vector2(0.5f, 0.38f), new Vector2(240f, 54f));
            buttonGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.14f);
            buttonGo.GetComponent<Button>().onClick.AddListener(Respawn);
            Text label = NewText(buttonGo.transform, "Label", "Respawn", font, 30, Color.white);
            Stretch(label.rectTransform);

            Text hint = NewText(deathGo.transform, "Hint", "or press Space", font, 20,
                new Color(0.6f, 0.6f, 0.6f));
            Place(hint.rectTransform, new Vector2(0.5f, 0.31f), new Vector2(400f, 30f));
        }

        private void Update()
        {
            NetworkPlayer local = NetworkClient.localPlayer != null
                ? NetworkClient.localPlayer.GetComponent<NetworkPlayer>()
                : null;

            bool playing = local != null;
            _barRoot.SetActive(playing);

            float dt = Time.deltaTime;
            if (playing)
            {
                float frac = local.MaxHealth > 0f ? Mathf.Clamp01(local.Health / local.MaxHealth) : 0f;
                _barFill.sizeDelta = new Vector2((BarWidth - 2f * BarPad) * frac, BarHeight - 2f * BarPad);
                _barFillImage.color = Color.Lerp(
                    new Color(0.85f, 0.2f, 0.15f, 0.95f), new Color(0.3f, 0.85f, 0.35f, 0.95f), frac);

                // Flash when health drops (ignore the first read and respawn refills).
                if (_prevHealth >= 0f && local.Health < _prevHealth - 0.01f)
                    _flashAlpha = 0.45f;
                _prevHealth = local.Health;
            }
            else
            {
                _prevHealth = -1f;
            }

            _flashAlpha = Mathf.MoveTowards(_flashAlpha, 0f, dt * 0.9f);
            _flash.color = new Color(0.8f, 0.05f, 0.05f, _flashAlpha);

            // Death screen fade + input.
            bool dead = playing && local.IsDead;
            _death.alpha = Mathf.MoveTowards(_death.alpha, dead ? 1f : 0f, dt * 3f);
            _death.blocksRaycasts = dead;
            _death.interactable = dead;
            CursorLock.ForceUnlock = dead;

            if (dead)
            {
                _causeText.text = local.DeathCause == NetworkPlayer.CauseOfDeath.Shark
                    ? "A shark got you."
                    : "You died.";
                if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                    Respawn();
            }
        }

        private void Respawn()
        {
            NetworkPlayer local = NetworkClient.localPlayer != null
                ? NetworkClient.localPlayer.GetComponent<NetworkPlayer>()
                : null;
            if (local != null) local.RequestRespawn();
        }

        // ---------- tiny uGUI builders ----------

        private static Image NewImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            return img;
        }

        private static Text NewText(Transform parent, string name, string content, Font font,
            int size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.text = content;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static void Place(RectTransform rect, Vector2 anchor, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }
    }
}
