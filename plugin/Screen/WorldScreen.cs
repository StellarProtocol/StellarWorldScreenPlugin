using System;
using UnityEngine;
using UnityEngine.UI;

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
        private int _texW, _texH;

        // World width of the screen in metres; height follows the frame aspect. Tunable in Milestone C.
        private float _widthMetres = 3f;

        /// <summary>The screen root transform (null until <see cref="EnsureCreated"/> runs).</summary>
        public Transform? Root => _root != null ? _root.transform : null;

        /// <summary>True once the screen GameObject exists.</summary>
        public bool Exists => _root != null;

        /// <summary>Creates the world-space canvas + image (once) and (re)allocates the texture for w×h.</summary>
        public void EnsureCreated(int w, int h)
        {
            if (_root == null)
            {
                _root = new GameObject("StellarWorldScreen");
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;

                var imageGo = new GameObject("Screen");
                imageGo.transform.SetParent(_root.transform, false);
                _image = imageGo.AddComponent<RawImage>();
                StretchToParent(_image.rectTransform);
            }
            EnsureTexture(w, h);
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

        /// <summary>Positions the screen a few metres ahead of the player at eye height, facing them.</summary>
        public void PlaceInFrontOfPlayer(Vector3 playerPos, Vector3 camForward, float distance = 4f)
        {
            if (_root == null) return;
            var fwd = camForward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            _root.transform.position = playerPos + fwd * distance + Vector3.up * 1.6f;
            // A uGUI canvas is visible from its -Z side, so orient -Z toward the player (i.e. +Z along fwd).
            _root.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
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
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; _image = null; }
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
