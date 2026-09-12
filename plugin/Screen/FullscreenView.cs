using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Il2CppInterop.Runtime;

namespace Stellar.WorldScreen.Screen
{
    /// <summary>
    /// A screen-space "cinema" overlay above ALL HUD/windows (sortingOrder 32756 &gt; framework windows 32755):
    /// a full-screen black backdrop with the video aspect-fit, plus a proper video-player control bar (custom
    /// uGUI on the same canvas so it draws above the video). The opaque backdrop hides every panel — the clean
    /// fullscreen the game menu gives. The bar carries play/pause, restart, mute, a click-to-seek progress bar
    /// with elapsed/total time, and exit; it auto-hides after a few idle seconds and returns on mouse movement.
    /// Fed the AVPro texture via <see cref="SetTexture"/> and ticked via <see cref="Tick"/>. Main thread only.
    /// </summary>
    internal sealed class FullscreenView
    {
        private const float IdleHideSeconds = 3f;

        private GameObject? _root;
        private RawImage? _image;
        private RectTransform? _imageRt;

        private CanvasGroup? _barGroup;
        private Image? _seekFill;
        private RectTransform? _seekTrackRt;
        private Text? _timeLabel;
        private Text? _playPauseLabel;
        private Text? _muteLabel;
        private Font? _font;

        private AvProPlayer? _player;
        private Action? _onStop;
        private readonly List<UnityAction> _clickRefs = new();

        private float _idle;
        private Vector3 _lastMouse;

        public bool Visible => _root != null && _root.activeSelf;

        /// <summary>Wires the bar to the player + a stop callback. Call once before first <see cref="Show"/>.</summary>
        public void Bind(AvProPlayer player, Action onStop) { _player = player; _onStop = onStop; }

        public void Show() { EnsureCreated(); _root!.SetActive(true); _idle = 0f; _lastMouse = Input.mousePosition; SetBarShown(true); }
        public void Hide() { if (_root != null) _root.SetActive(false); }
        public void Toggle() { if (Visible) Hide(); else Show(); }

        /// <summary>Assigns the current video texture and aspect-fits it (call each frame while visible).</summary>
        public void SetTexture(Texture tex, int vw, int vh)
        {
            if (_image == null || tex == null) return;
            if (!ReferenceEquals(_image.texture, tex)) _image.texture = tex;
            float sw = UnityEngine.Screen.width, sh = UnityEngine.Screen.height;
            if (sw > 0f && sh > 0f)
            {
                float va = (vw > 0 && vh > 0) ? (float)vw / vh : 16f / 9f;
                float sa = sw / sh;
                float w, h;
                if (sa > va) { h = sh; w = sh * va; } else { w = sw; h = sw / va; }
                if (_imageRt != null) _imageRt.sizeDelta = new Vector2(w, h);
            }
        }

        /// <summary>Per-frame: refresh labels/seek bar and run the auto-hide drawer (call each frame while shown).</summary>
        public void Tick(float dt)
        {
            if (_player == null) return;
            RefreshBar();

            var mouse = Input.mousePosition;
            if ((mouse - _lastMouse).sqrMagnitude > 1f) { _lastMouse = mouse; _idle = 0f; }
            else _idle += dt;
            SetBarShown(_idle < IdleHideSeconds);
        }

        public void Destroy()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; _image = null; _imageRt = null; }
            _clickRefs.Clear();
        }

        // --- build ---

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("StellarFullscreen");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32756;            // above framework windows (32755) → covers every panel
            _root.AddComponent<GraphicRaycaster>();

            var bg = NewImage(_root.transform, "Backdrop", Color.black);
            StretchFull(bg.rectTransform);

            var imgGo = new GameObject("Video");
            imgGo.transform.SetParent(_root.transform, false);
            _image = imgGo.AddComponent<RawImage>();
            _imageRt = _image.rectTransform;
            _imageRt.anchorMin = _imageRt.anchorMax = new Vector2(0.5f, 0.5f);
            _imageRt.pivot = new Vector2(0.5f, 0.5f);
            _imageRt.anchoredPosition = Vector2.zero;
            _image.uvRect = new Rect(0f, 1f, 1f, -1f);  // AVPro texture upright with this flip (matches world screen)

            _font = ResolveFont();
            BuildControlBar();
        }

        private void BuildControlBar()
        {
            // Bar: bottom, stretched horizontally with side margins, 96 px tall, translucent.
            var bar = NewImage(_root!.transform, "ControlBar", new Color(0.05f, 0.05f, 0.07f, 0.78f));
            var barRt = bar.rectTransform;
            barRt.anchorMin = new Vector2(0f, 0f);
            barRt.anchorMax = new Vector2(1f, 0f);
            barRt.pivot = new Vector2(0.5f, 0f);
            barRt.offsetMin = new Vector2(60f, 40f);    // left 60, bottom 40
            barRt.offsetMax = new Vector2(-60f, 136f);  // right 60, top = 40 + 96
            _barGroup = bar.gameObject.AddComponent<CanvasGroup>();

            // Seek track along the top of the bar (click to seek), with a fill.
            var track = NewImage(bar.transform, "SeekTrack", new Color(1f, 1f, 1f, 0.20f));
            _seekTrackRt = track.rectTransform;
            _seekTrackRt.anchorMin = new Vector2(0f, 1f);
            _seekTrackRt.anchorMax = new Vector2(1f, 1f);
            _seekTrackRt.pivot = new Vector2(0.5f, 1f);
            _seekTrackRt.offsetMin = new Vector2(18f, -30f);
            _seekTrackRt.offsetMax = new Vector2(-18f, -22f); // 8 px tall, 22 px below bar top
            var trackBtn = track.gameObject.AddComponent<Button>();
            trackBtn.targetGraphic = track;
            AddClick(trackBtn, OnSeekClick);

            _seekFill = NewImage(track.transform, "Fill", new Color(0.72f, 0.36f, 1f, 0.95f)); // accent purple
            _seekFill.type = Image.Type.Filled;
            _seekFill.fillMethod = Image.FillMethod.Horizontal;
            _seekFill.fillOrigin = 0; // 0 = Left for horizontal fill
            _seekFill.fillAmount = 0f;
            StretchFull(_seekFill.rectTransform);

            // Left cluster: play/pause, restart, mute.
            _playPauseLabel = AddButton(bar.transform, "Play", () => _player?.TogglePause(), anchorLeft: true, x: 18f, w: 104f);
            AddButton(bar.transform, "Restart", () => _player?.Seek(0d), anchorLeft: true, x: 130f, w: 92f);
            _muteLabel = AddButton(bar.transform, "Mute", () => _player?.ToggleMute(), anchorLeft: true, x: 230f, w: 92f);

            // Centre: elapsed / total time.
            _timeLabel = NewText(bar.transform, "0:00 / 0:00");
            var tRt = _timeLabel.rectTransform;
            tRt.anchorMin = tRt.anchorMax = new Vector2(0.5f, 0f);
            tRt.pivot = new Vector2(0.5f, 0f);
            tRt.anchoredPosition = new Vector2(0f, 14f);
            tRt.sizeDelta = new Vector2(220f, 34f);

            // Right cluster: exit.
            AddButton(bar.transform, "Exit full screen", () => Hide(), anchorLeft: false, x: -18f, w: 156f);
        }

        // --- per-frame refresh ---

        private void RefreshBar()
        {
            if (_player == null) return;
            if (_playPauseLabel != null) _playPauseLabel.text = _player.IsPlaying ? "Pause" : "Play";
            if (_muteLabel != null) _muteLabel.text = _player.IsMuted ? "Unmute" : "Mute";
            double dur = _player.Duration, cur = _player.CurrentTime;
            if (_seekFill != null) _seekFill.fillAmount = dur > 0.01 ? Mathf.Clamp01((float)(cur / dur)) : 0f;
            if (_timeLabel != null) _timeLabel.text = $"{Fmt(cur)} / {Fmt(dur)}";
        }

        private void OnSeekClick()
        {
            if (_player == null || _seekTrackRt == null) return;
            double dur = _player.Duration;
            if (dur <= 0.01) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_seekTrackRt, Input.mousePosition, null, out var local))
            {
                // pivot is centre-x; convert to 0..1 across the track width.
                float w = _seekTrackRt.rect.width;
                float frac = Mathf.Clamp01((local.x + w * _seekTrackRt.pivot.x) / w);
                _player.Seek(frac * dur);
            }
        }

        private void SetBarShown(bool shown)
        {
            if (_barGroup == null) return;
            _barGroup.alpha = Mathf.MoveTowards(_barGroup.alpha, shown ? 1f : 0f, Time.unscaledDeltaTime * 6f);
            _barGroup.blocksRaycasts = shown;
            _barGroup.interactable = shown;
        }

        private static string Fmt(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            int s = (int)seconds;
            return $"{s / 60}:{s % 60:00}";
        }

        // --- uGUI helpers ---

        private Text? AddButton(Transform parent, string label, Action onClick, bool anchorLeft, float x, float w)
        {
            var img = NewImage(parent, "Btn", new Color(0.16f, 0.16f, 0.21f, 0.96f));
            var rt = img.rectTransform;
            var ax = anchorLeft ? 0f : 1f;
            rt.anchorMin = rt.anchorMax = new Vector2(ax, 0f);
            rt.pivot = new Vector2(ax, 0f);
            rt.anchoredPosition = new Vector2(x, 14f);
            rt.sizeDelta = new Vector2(w, 40f);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            AddClick(btn, onClick);
            var text = NewText(img.transform, label);
            StretchFull(text.rectTransform);
            return text;
        }

        private void AddClick(Button btn, Action onClick)
        {
            var ua = DelegateSupport.ConvertDelegate<UnityAction>(onClick);
            if (ua != null) { _clickRefs.Add(ua); btn.onClick.AddListener(ua); }
        }

        private static Image NewImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        private Text NewText(Transform parent, string content)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.text = content;
            if (_font != null) t.font = _font;
            t.fontSize = 16;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            return t;
        }

        private static Font? ResolveFont()
        {
            Font? f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { } }
            if (f == null) { try { var all = Resources.FindObjectsOfTypeAll<Font>(); if (all != null && all.Length > 0) f = all[0]; } catch (Exception) { } }
            if (f == null) { try { f = Font.CreateDynamicFontFromOSFont("Arial", 16); } catch (Exception) { } }
            return f;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
