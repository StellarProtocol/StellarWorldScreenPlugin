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
    /// a full-screen black backdrop with the video aspect-fit on top, and its OWN bottom control bar (custom
    /// uGUI, so it renders above the video). Because it sits above every framework window, its opaque backdrop
    /// hides the game HUD, the framework HUD, AND other plugins' meters/panels — the clean fullscreen the game
    /// menu gives. The same AVPro texture the world screen shows is fed via <see cref="SetTexture"/>. Main thread.
    /// </summary>
    internal sealed class FullscreenView
    {
        private GameObject? _root;
        private RawImage? _image;
        private RectTransform? _imageRt;
        private Text? _playPauseLabel;

        private Func<bool>? _isPlaying;
        private Action? _togglePause;
        private Action? _stop;
        private Action? _exit;
        // Kept referenced so the native onClick callbacks aren't GC'd (crashes the click otherwise).
        private readonly List<UnityAction> _clickRefs = new();
        private Font? _font;

        public bool Visible => _root != null && _root.activeSelf;

        /// <summary>Wires the control-bar actions. Call once before first <see cref="Show"/>.</summary>
        public void Bind(Func<bool> isPlaying, Action togglePause, Action stop, Action exit)
        {
            _isPlaying = isPlaying; _togglePause = togglePause; _stop = stop; _exit = exit;
        }

        public void Show() { EnsureCreated(); _root!.SetActive(true); }
        public void Hide() { if (_root != null) _root.SetActive(false); }
        public void Toggle() { if (Visible) Hide(); else Show(); }

        /// <summary>Assigns the current video texture, aspect-fits it, and refreshes the play/pause label.</summary>
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
            if (_playPauseLabel != null && _isPlaying != null)
                _playPauseLabel.text = _isPlaying() ? "Pause" : "Play";
        }

        public void Destroy()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; _image = null; _imageRt = null; _playPauseLabel = null; }
            _clickRefs.Clear();
        }

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("StellarFullscreen");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32756;               // above framework windows (32755) → covers every panel
            _root.AddComponent<GraphicRaycaster>();     // so the control-bar buttons receive clicks

            var bgGo = new GameObject("Backdrop");
            bgGo.transform.SetParent(_root.transform, false);
            var bg = bgGo.AddComponent<Image>();
            bg.color = Color.black;
            StretchFull(bg.rectTransform);

            var imgGo = new GameObject("Video");
            imgGo.transform.SetParent(_root.transform, false);
            _image = imgGo.AddComponent<RawImage>();
            _imageRt = _image.rectTransform;
            _imageRt.anchorMin = _imageRt.anchorMax = new Vector2(0.5f, 0.5f);
            _imageRt.pivot = new Vector2(0.5f, 0.5f);
            _imageRt.anchoredPosition = Vector2.zero;
            _image.uvRect = new Rect(0f, 1f, 1f, -1f);  // AVPro texture is upright with this flip (matches world screen)

            BuildControlBar();
        }

        // A bottom-centre bar with three buttons. Manual layout (no LayoutGroup) to keep it simple in IL2CPP.
        private void BuildControlBar()
        {
            _font = ResolveFont();

            var bar = new GameObject("ControlBar");
            bar.transform.SetParent(_root!.transform, false);
            var barImg = bar.AddComponent<Image>();
            barImg.color = new Color(0f, 0f, 0f, 0.55f);
            var barRt = barImg.rectTransform;
            barRt.anchorMin = barRt.anchorMax = new Vector2(0.5f, 0f);
            barRt.pivot = new Vector2(0.5f, 0f);
            barRt.anchoredPosition = new Vector2(0f, 28f);
            barRt.sizeDelta = new Vector2(500f, 56f);

            AddButton(bar.transform, () => _isPlaying != null && _isPlaying() ? "Pause" : "Play",
                () => _togglePause?.Invoke(), -165f, out _playPauseLabel);
            AddButton(bar.transform, () => "Stop", () => _stop?.Invoke(), 0f, out _);
            AddButton(bar.transform, () => "Exit full screen", () => _exit?.Invoke(), 165f, out _);
        }

        private void AddButton(Transform parent, Func<string> label, Action onClick, float x, out Text? text)
        {
            var go = new GameObject("Btn");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.16f, 0.2f, 0.95f);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(155f, 40f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var ua = DelegateSupport.ConvertDelegate<UnityAction>(onClick);
            if (ua != null) { _clickRefs.Add(ua); btn.onClick.AddListener(ua); }

            var tgo = new GameObject("Label");
            tgo.transform.SetParent(go.transform, false);
            text = tgo.AddComponent<Text>();
            text.text = label();
            if (_font != null) text.font = _font;
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            StretchFull(text.rectTransform);
        }

        // A usable uGUI Font under Proton (built-in first, then any loaded Font, then a dynamic OS font).
        private static Font? ResolveFont()
        {
            Font? f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { } }
            if (f == null)
            {
                try { var all = Resources.FindObjectsOfTypeAll<Font>(); if (all != null && all.Length > 0) f = all[0]; }
                catch (Exception) { }
            }
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
