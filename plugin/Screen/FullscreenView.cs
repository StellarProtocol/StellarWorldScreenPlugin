using UnityEngine;
using UnityEngine.UI;

namespace Stellar.WorldScreen.Screen
{
    /// <summary>
    /// A screen-space "cinema" overlay: a full-screen black backdrop with the video texture centered and
    /// aspect-fit on top. Toggled from the proximity action menu; the same externally-owned AVPro texture
    /// the world screen shows is fed here via <see cref="SetTexture"/> each frame while visible. Main thread only.
    /// </summary>
    internal sealed class FullscreenView
    {
        private GameObject? _root;
        private RawImage? _image;
        private RectTransform? _imageRt;

        /// <summary>True while the overlay is shown.</summary>
        public bool Visible => _root != null && _root.activeSelf;

        /// <summary>Shows the overlay (creating it once).</summary>
        public void Show() { EnsureCreated(); _root!.SetActive(true); }

        /// <summary>Hides the overlay.</summary>
        public void Hide() { if (_root != null) _root.SetActive(false); }

        /// <summary>Shows if hidden, hides if shown.</summary>
        public void Toggle() { if (Visible) Hide(); else Show(); }

        /// <summary>Assigns the current video texture and aspect-fits it to the screen (call each frame while shown).</summary>
        public void SetTexture(Texture tex, int vw, int vh)
        {
            if (_image == null || tex == null) return;
            if (!ReferenceEquals(_image.texture, tex)) _image.texture = tex;

            float sw = UnityEngine.Screen.width, sh = UnityEngine.Screen.height;
            if (sw <= 0f || sh <= 0f) return;
            float va = (vw > 0 && vh > 0) ? (float)vw / vh : 16f / 9f;
            float sa = sw / sh;
            float w, h;
            if (sa > va) { h = sh; w = sh * va; } // screen wider than video → pillarbox
            else { w = sw; h = sw / va; }          // screen taller than video → letterbox
            if (_imageRt != null) _imageRt.sizeDelta = new Vector2(w, h);
        }

        /// <summary>Destroys the overlay.</summary>
        public void Destroy()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; _image = null; _imageRt = null; }
        }

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("StellarFullscreen");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000; // above the game HUD and framework overlay

            // Black backdrop filling the screen (Image with no sprite draws its solid colour).
            var bgGo = new GameObject("Backdrop");
            bgGo.transform.SetParent(_root.transform, false);
            var bg = bgGo.AddComponent<Image>();
            bg.color = Color.black;
            StretchFull(bg.rectTransform);

            // Centered video, sized by SetTexture to preserve aspect.
            var imgGo = new GameObject("Video");
            imgGo.transform.SetParent(_root.transform, false);
            _image = imgGo.AddComponent<RawImage>();
            _imageRt = _image.rectTransform;
            _imageRt.anchorMin = _imageRt.anchorMax = new Vector2(0.5f, 0.5f);
            _imageRt.pivot = new Vector2(0.5f, 0.5f);
            _imageRt.anchoredPosition = Vector2.zero;
            // AVPro's texture displays upright on the world screen with this vertical flip; match it here.
            _image.uvRect = new Rect(0f, 1f, 1f, -1f);
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
