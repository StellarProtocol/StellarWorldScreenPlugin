using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Il2CppInterop.Runtime;
using RenderHeads.Media.AVProVideo;

namespace Stellar.WorldScreen.Screen
{
    /// <summary>
    /// A screen-space "cinema" overlay above ALL HUD/windows (sortingOrder 32756 &gt; framework windows 32755):
    /// a full-screen black backdrop with the video rendered via AVPro's <see cref="DisplayUGUI"/> (correct
    /// material — YCbCr/colour space/orientation), plus a video-player control bar (custom uGUI on the same
    /// canvas). The bar has play/pause, restart, mute, a draggable volume slider, a draggable seek bar with a
    /// knob + elapsed/total time, and exit; it auto-hides after a few idle seconds and returns on mouse
    /// movement (or while dragging). Ticked via <see cref="Tick"/>. Main thread only.
    /// </summary>
    internal sealed class FullscreenView
    {
        private const float IdleHideSeconds = 3f;

        private GameObject? _root;
        private DisplayUGUI? _display; // AVPro's uGUI renderer (correct colour, unlike a plain RawImage)

        private CanvasGroup? _barGroup;
        private Text? _timeLabel;
        private Text? _playPauseLabel;
        private Text? _muteLabel;
        private Font? _font;

        // Seek + volume sliders (track / fill / knob).
        private RectTransform? _seekTrackRt;
        private Image? _seekFill;
        private RectTransform? _seekKnob;
        private RectTransform? _volTrackRt;
        private Image? _volFill;
        private RectTransform? _volKnob;
        private bool _seekDrag, _volDrag;
        private bool _wasHeld;      // previous frame's mouse-held state, for our own press edge
        private float _seekPreview; // fraction shown while dragging the seek knob; committed on release

        private AvProPlayer? _player;
        private Action? _onStop;
        private readonly List<UnityAction> _clickRefs = new();

        private float _idle;
        private Vector3 _lastMouse;

        public bool Visible => _root != null && _root.activeSelf;

        /// <summary>Wires the bar to the player + a stop callback. Call once before first <see cref="Show"/>.</summary>
        public void Bind(AvProPlayer player, Action onStop) { _player = player; _onStop = onStop; }

        public void Show() { EnsureCreated(); _root!.SetActive(true); _idle = 0f; _lastMouse = Input.mousePosition; SetBarShown(true, instant: true); }
        public void Hide() { if (_root != null) _root.SetActive(false); }
        public void Toggle() { if (Visible) Hide(); else Show(); }

        /// <summary>Per-frame: refresh labels/sliders, handle drags, and run the auto-hide drawer.</summary>
        public void Tick(float dt)
        {
            if (_player == null) return;
            HandleDrags();
            RefreshBar();

            var mouse = Input.mousePosition;
            bool active = (mouse - _lastMouse).sqrMagnitude > 1f || _seekDrag || _volDrag;
            if (active) { _lastMouse = mouse; _idle = 0f; } else _idle += dt;
            SetBarShown(_idle < IdleHideSeconds, instant: false);
        }

        public void Destroy()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; _display = null; }
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

            var videoGo = new GameObject("Video");
            videoGo.transform.SetParent(_root.transform, false);
            _display = videoGo.AddComponent<DisplayUGUI>();
            StretchFull(_display.rectTransform);
            if (_player?.Player != null) _display.CurrentMediaPlayer = _player.Player;

            _font = ResolveFont();
            BuildControlBar();
        }

        private void BuildControlBar()
        {
            var bar = NewImage(_root!.transform, "ControlBar", new Color(0.05f, 0.05f, 0.07f, 0.80f));
            var barRt = bar.rectTransform;
            barRt.anchorMin = new Vector2(0f, 0f);
            barRt.anchorMax = new Vector2(1f, 0f);
            barRt.pivot = new Vector2(0.5f, 0f);
            barRt.offsetMin = new Vector2(60f, 40f);
            barRt.offsetMax = new Vector2(-60f, 140f); // 100 px tall
            _barGroup = bar.gameObject.AddComponent<CanvasGroup>();

            // Seek bar along the top of the bar.
            (_seekTrackRt, _seekFill, _seekKnob) = BuildSlider(bar.transform, "Seek",
                new Color(1f, 1f, 1f, 0.20f), new Color(0.72f, 0.36f, 1f, 0.95f),
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f), pivot: new Vector2(0.5f, 1f),
                offsetMin: new Vector2(18f, -34f), offsetMax: new Vector2(-18f, -22f)); // 12 px tall

            // Buttons row (bottom).
            _playPauseLabel = AddButton(bar.transform, "Play", () => _player?.TogglePause(), true, 18f, 104f);
            AddButton(bar.transform, "Restart", () => _player?.Seek(0d), true, 130f, 92f);
            _muteLabel = AddButton(bar.transform, "Mute", () => _player?.ToggleMute(), true, 230f, 84f);

            // Volume slider (compact, in the left cluster, vertically centred on the button row at y≈34).
            (_volTrackRt, _volFill, _volKnob) = BuildSlider(bar.transform, "Vol",
                new Color(1f, 1f, 1f, 0.20f), new Color(0.85f, 0.85f, 0.9f, 0.95f),
                anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(0f, 0f), pivot: new Vector2(0f, 0.5f),
                offsetMin: new Vector2(328f, 31f), offsetMax: new Vector2(448f, 37f));

            _timeLabel = NewText(bar.transform, "0:00 / 0:00");
            var tRt = _timeLabel.rectTransform;
            tRt.anchorMin = tRt.anchorMax = new Vector2(0.5f, 0f);
            tRt.pivot = new Vector2(0.5f, 0f);
            tRt.anchoredPosition = new Vector2(0f, 14f);
            tRt.sizeDelta = new Vector2(220f, 34f);

            AddButton(bar.transform, "Exit full screen", () => Hide(), false, -18f, 156f);
        }

        // Builds a track + fill + knob. offsetMin/Max place the TRACK relative to the given anchors.
        private (RectTransform track, Image fill, RectTransform knob) BuildSlider(
            Transform parent, string name, Color trackColor, Color fillColor,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 offsetMin, Vector2 offsetMax)
        {
            var track = NewImage(parent, name + "Track", trackColor);
            var trackRt = track.rectTransform;
            trackRt.anchorMin = anchorMin; trackRt.anchorMax = anchorMax; trackRt.pivot = pivot;
            trackRt.offsetMin = offsetMin; trackRt.offsetMax = offsetMax;

            var fill = NewImage(track.transform, name + "Fill", fillColor);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = 0;
            fill.fillAmount = 0f;
            StretchFull(fill.rectTransform);

            var knobImg = NewImage(track.transform, name + "Knob", Color.white);
            var knob = knobImg.rectTransform;
            knob.anchorMin = knob.anchorMax = new Vector2(0f, 0.5f);
            knob.pivot = new Vector2(0.5f, 0.5f);
            knob.sizeDelta = new Vector2(14f, 14f);
            knob.anchoredPosition = Vector2.zero;
            return (trackRt, fill, knob);
        }

        // --- per-frame ---

        private void HandleDrags()
        {
            if (_player == null) return;

            // Drive everything off the HELD state (level), not the Up/Down edges — GetMouseButtonUp is
            // unreliable under this game's input, which left a click "stuck" following the mouse. A drag is
            // therefore active ONLY while the button is genuinely held; it can never persist past release.
            bool held = Input.GetMouseButton(0);
            bool press = held && !_wasHeld;
            _wasHeld = held;

            if (press)
            {
                if (OverPadded(_seekTrackRt, 16f)) { _seekDrag = true; Frac(_seekTrackRt, out _seekPreview); }
                else if (OverPadded(_volTrackRt, 16f)) _volDrag = true;
            }

            if (held)
            {
                if (_seekDrag && Frac(_seekTrackRt, out var sf)) _seekPreview = sf;   // preview only — no seek yet
                if (_volDrag && Frac(_volTrackRt, out var vf)) _player.SetVolume(vf); // volume is cheap → live
            }
            else if (_seekDrag || _volDrag)
            {
                // Released: commit a pending seek, then clear so nothing keeps following the mouse.
                if (_seekDrag) { double dur = _player.Duration; if (dur > 0.01) _player.Seek(_seekPreview * dur); }
                _seekDrag = false; _volDrag = false;
            }
        }

        private void RefreshBar()
        {
            if (_player == null) return;
            if (_playPauseLabel != null) _playPauseLabel.text = _player.IsPlaying ? "Pause" : "Play";
            if (_muteLabel != null) _muteLabel.text = _player.IsMuted ? "Unmute" : "Mute";

            double dur = _player.Duration, cur = _player.CurrentTime;
            float sf = _seekDrag ? _seekPreview : (dur > 0.01 ? Mathf.Clamp01((float)(cur / dur)) : 0f);
            SetSlider(_seekFill, _seekKnob, _seekTrackRt, sf);
            double showTime = _seekDrag ? _seekPreview * dur : cur; // preview the target time while dragging
            if (_timeLabel != null) _timeLabel.text = $"{Fmt(showTime)} / {Fmt(dur)}";

            float volFrac = _player.IsMuted ? 0f : Mathf.Clamp01(_player.Volume);
            SetSlider(_volFill, _volKnob, _volTrackRt, volFrac);
        }

        private static void SetSlider(Image? fill, RectTransform? knob, RectTransform? track, float frac)
        {
            if (fill != null) fill.fillAmount = frac;
            if (knob != null && track != null) knob.anchoredPosition = new Vector2(frac * track.rect.width, 0f);
        }

        private void SetBarShown(bool shown, bool instant)
        {
            if (_barGroup == null) return;
            float target = shown ? 1f : 0f;
            _barGroup.alpha = instant ? target : Mathf.MoveTowards(_barGroup.alpha, target, Time.unscaledDeltaTime * 6f);
            _barGroup.blocksRaycasts = shown;
            _barGroup.interactable = shown;
        }

        // Mouse within the track's rect, expanded by padY vertically (and a little horizontally) so the thin
        // track/knob is easy to grab.
        private static bool OverPadded(RectTransform? rt, float padY)
        {
            if (rt == null) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, null, out var local))
                return false;
            var r = rt.rect;
            return local.x >= r.xMin - 6f && local.x <= r.xMax + 6f && local.y >= r.yMin - padY && local.y <= r.yMax + padY;
        }

        // Fraction 0..1 of the mouse X across the track (accounts for pivot).
        private static bool Frac(RectTransform? track, out float frac)
        {
            frac = 0f;
            if (track == null) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(track, Input.mousePosition, null, out var local))
                return false;
            float w = track.rect.width;
            if (w <= 0f) return false;
            frac = Mathf.Clamp01((local.x + w * track.pivot.x) / w);
            return true;
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
            var ua = DelegateSupport.ConvertDelegate<UnityAction>(onClick);
            if (ua != null) { _clickRefs.Add(ua); btn.onClick.AddListener(ua); }
            var text = NewText(img.transform, label);
            StretchFull(text.rectTransform);
            return text;
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
