using System;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

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

        // 3D positional audio: an AudioSource on the (world-positioned) root pulls PCM from _audio via a
        // streaming AudioClip. Volume falls off with the listener's distance from the screen (see EnsureAudio).
        private AudioSource? _audioSource;
        private AudioClip? _audioClip;
        private AudioSink? _audio;
        // Unity's audio thread invokes _pcmCallback; it MUST stay referenced (GC root) or the native call
        // crashes. _pcmScratch bridges the interop Il2CppStructArray<float> to AudioSink's float[] API.
        private AudioClip.PCMReaderCallback? _pcmCallback;
        private float[] _pcmScratch = System.Array.Empty<float>();

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
                // ffmpeg delivers frame rows top-to-bottom; a Unity Texture2D's origin is bottom-left, so
                // raw upload displays upside-down. Flip vertically via the UV rect (free — no per-frame cost).
                _image.uvRect = new Rect(0f, 1f, 1f, -1f);
            }
            EnsureAudio();
            EnsureTexture(w, h);
        }

        /// <summary>
        /// Registers the PCM ring buffer the screen's 3D <see cref="AudioSource"/> pulls from. Call once,
        /// before the screen is created; <see cref="EnsureAudio"/> then builds the streaming clip on creation.
        /// </summary>
        public void AttachAudio(AudioSink sink) => _audio = sink;

        // Builds the streaming AudioClip + 3D AudioSource on the root once (idempotent). The AudioSource lives
        // on the world-positioned root, so Unity attenuates it by the listener's (player/camera) distance:
        // full volume within minDistance, fading linearly to silence at maxDistance — "quieter when far, gone
        // when very far". The clip is streamed: Unity invokes _audio.ReadInto on the audio thread to fill it,
        // and _audio hands back silence on underrun so a gap never desyncs playback.
        private void EnsureAudio()
        {
            if (_root == null || _audio == null || _audioSource != null) return;
            // Streaming clip: Unity calls _pcmCallback on the audio thread to pull the next PCM chunk.
            // Il2CppInterop delegates aren't constructed from a managed method group directly — convert one.
            _pcmCallback = DelegateSupport.ConvertDelegate<AudioClip.PCMReaderCallback>(
                (Action<Il2CppStructArray<float>>)OnPcmRead);
            _audioClip = AudioClip.Create("StellarWorldScreenAudio", 48000, 2, 48000, true, _pcmCallback);
            _audioSource = _root.AddComponent<AudioSource>();
            _audioSource.clip = _audioClip;
            _audioSource.loop = true;
            _audioSource.playOnAwake = true;                    // resume when the root is re-activated (SetVisible)
            _audioSource.spatialBlend = 1f;                     // fully 3D — position-attenuated, not 2D flat
            _audioSource.rolloffMode = AudioRolloffMode.Linear; // linear fade → truly silent past maxDistance
            _audioSource.minDistance = 3f;                      // full volume within 3 m of the screen
            _audioSource.maxDistance = 40f;                     // inaudible beyond 40 m
            _audioSource.dopplerLevel = 0f;                     // a screen shouldn't pitch-shift as you move
            _audioSource.Play();
        }

        // Unity audio thread: fill `data` (interleaved stereo floats) with the next PCM from the sink. The
        // sink hands back silence on underrun, so a starved buffer plays a gap rather than desyncing. Bridges
        // the interop array through a same-length reusable scratch so AudioSink stays pure BCL (float[] only).
        private void OnPcmRead(Il2CppStructArray<float> data)
        {
            if (_audio == null) return;
            int n = data.Length;
            if (n <= 0) return;
            if (_pcmScratch.Length != n) _pcmScratch = new float[n];
            _audio.ReadInto(_pcmScratch);
            for (int i = 0; i < n; i++) data[i] = _pcmScratch[i];
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

        /// <summary>Destroys the screen, its texture, and its audio clip.</summary>
        public void Destroy()
        {
            if (_audioSource != null) { _audioSource.Stop(); _audioSource = null; }
            if (_audioClip != null) { UnityEngine.Object.Destroy(_audioClip); _audioClip = null; }
            _pcmCallback = null;                        // release the GC root now the audio thread is done
            _pcmScratch = System.Array.Empty<float>();
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
