using System;
using UnityEngine;
using UnityEngine.UI;
using RenderHeads.Media.AVProVideo;

namespace Stellar.WorldScreen.Screen
{
    /// <summary>
    /// A placeable video screen in the game world: a world-space <see cref="Canvas"/> carrying a
    /// <see cref="RawImage"/> textured by a reused <see cref="Texture2D"/>. Frame bytes are uploaded
    /// via <see cref="Upload"/> each frame (main-thread only — all Unity calls here must run on the
    /// main thread). Milestone-A scope: create, upload, place-in-front-of-player, show/hide, destroy.
    /// Full move/rotate/scale controls land in Milestone C.
    /// </summary>
    internal sealed class WorldScreen
    {
        private GameObject? _root;
        private RawImage? _image;
        private Texture2D? _tex;
        private DisplayUGUI? _display; // AVPro's uGUI renderer (correct colour, unlike a plain RawImage)
        private int _texW, _texH;

        // Audio is NOT played through Unity here: this game routes audio through Wwise with Unity's own audio
        // engine disabled (a Unity AudioSource is silent — dspTime never advances). Playback happens in the
        // helper process (ffplay); the plugin drives distance-based volume off this screen's world position
        // (see WorldScreenPlugin.PumpVolume, which reads Root).

        // World width of the screen in metres; height follows the frame aspect. Tunable in Milestone C.
        private float _widthMetres = 3f;

        /// <summary>The screen root transform (null until <see cref="EnsureCreated"/> runs).</summary>
        public Transform? Root => _root != null ? _root.transform : null;

        /// <summary>True once the screen GameObject exists.</summary>
        public bool Exists => _root != null;

        // Creates the world-space canvas root (once).
        private void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("StellarWorldScreen");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
        }

        /// <summary>Creates the world-space canvas + RawImage (once, wire path) and (re)allocates the texture.</summary>
        public void EnsureCreated(int w, int h)
        {
            EnsureRoot();
            if (_image == null)
            {
                var imageGo = new GameObject("Screen");
                imageGo.transform.SetParent(_root!.transform, false);
                _image = imageGo.AddComponent<RawImage>();
                StretchToParent(_image.rectTransform);
                // ffmpeg delivers frame rows top-to-bottom; a Unity Texture2D's origin is bottom-left, so
                // raw upload displays upside-down. Flip vertically via the UV rect (free — no per-frame cost).
                _image.uvRect = new Rect(0f, 1f, 1f, -1f);
            }
            EnsureTexture(w, h);
        }

        /// <summary>
        /// Renders <paramref name="player"/>'s video on the screen via AVPro's <see cref="DisplayUGUI"/> (the
        /// correct material — YCbCr/colour space/orientation), sizing the canvas to w×h. Binds once; safe to
        /// call each frame. Main thread only.
        /// </summary>
        public void ShowVideoPlayer(MediaPlayer? player, int w, int h)
        {
            if (player == null || w <= 0 || h <= 0) return;
            EnsureRoot();
            if (_display == null)
            {
                var go = new GameObject("ScreenVideo");
                go.transform.SetParent(_root!.transform, false);
                _display = go.AddComponent<DisplayUGUI>();
                StretchToParent(_display.rectTransform);
            }
            if (_display.CurrentMediaPlayer != player) _display.CurrentMediaPlayer = player;
            if (_texW != w || _texH != h) { _texW = w; _texH = h; SizeCanvas(w, h); }
        }

        /// <summary>Uploads a raw RGBA32 frame (length ≥ w*h*4) into the screen texture. Main thread only.</summary>
        public void Upload(byte[] rgba, int w, int h)
        {
            if (rgba == null || w <= 0 || h <= 0) return;
            EnsureCreated(w, h);
            if (_tex == null) return;
            // LoadRawTextureData wants exactly w*h*4 bytes for RGBA32; the sink hands a buffer that may be
            // larger than the payload, so copy the exact frame span into the texture's native buffer.
            _tex.LoadRawTextureData(rgba);
            _tex.Apply(false);
        }

        /// <summary>
        /// Positions the screen <paramref name="distance"/> metres ahead of <paramref name="origin"/> along
        /// the horizontal projection of <paramref name="forward"/>, facing back toward the origin. The caller
        /// chooses the origin (e.g. the active camera's position, already at eye height).
        /// </summary>
        public void PlaceInFrontOf(Vector3 origin, Vector3 forward, float distance = 4f)
        {
            if (_root == null) return;
            var fwd = forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            // Raise the centre so a ~1.7 m-tall screen clears the ground and sits nearer eye level (the
            // camera origin can be low in third-person). The user fine-tunes with the overlay controls.
            _root.transform.position = origin + fwd * distance + Vector3.up * 1.6f;
            // A uGUI canvas renders on its +Z face; orient +Z along fwd so the visible side faces the origin.
            _root.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        }

        /// <summary>Moves the screen up (+) or down (-) in world space.</summary>
        public void MoveVertical(float dy)
        {
            if (_root != null) _root.transform.position += new Vector3(0f, dy, 0f);
        }

        /// <summary>Moves the screen farther (+) or nearer (-) along its own facing axis.</summary>
        public void MoveDepth(float dz)
        {
            if (_root != null) _root.transform.position += _root.transform.forward * dz;
        }

        /// <summary>Scales the screen by <paramref name="factor"/> (clamped to a sane world-width range).</summary>
        public void ScaleBy(float factor)
        {
            _widthMetres = Mathf.Clamp(_widthMetres * factor, 0.75f, 40f);
            if (_texW > 0 && _texH > 0) SizeCanvas(_texW, _texH);
        }

        /// <summary>Shows or hides the screen without destroying it.</summary>
        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        /// <summary>Destroys the screen and its texture.</summary>
        public void Destroy()
        {
            if (_tex != null) { UnityEngine.Object.Destroy(_tex); _tex = null; }
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; _image = null; _display = null; }
            _texW = _texH = 0;
        }

        private void EnsureTexture(int w, int h)
        {
            if (_tex != null && _texW == w && _texH == h) return;
            if (_tex != null) UnityEngine.Object.Destroy(_tex);
            _tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            _texW = w;
            _texH = h;
            if (_image != null) _image.texture = _tex;
            SizeCanvas(w, h);
        }

        // Sizes the canvas rect to the frame pixels, then scales the whole root so the world width is
        // _widthMetres (height follows the frame aspect).
        private void SizeCanvas(int w, int h)
        {
            if (_root == null) return;
            var rt = _root.GetComponent<RectTransform>();
            if (rt != null) rt.sizeDelta = new Vector2(w, h);
            float scale = w > 0 ? _widthMetres / w : 0.005f;
            _root.transform.localScale = new Vector3(scale, scale, scale);
        }

        private static void StretchToParent(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
