using UnityEngine;
using RenderHeads.Media.AVProVideo;

namespace Stellar.WorldScreen.Screen
{
    /// <summary>
    /// Video playback via the game's own AVPro <see cref="MediaPlayer"/>: hardware decode + internal A/V
    /// sync, rendered to a Unity <see cref="Texture"/> we hand to the world screen's RawImage. Audio uses
    /// AVPro's <see cref="AudioOutput.System"/> path (DirectShow/MediaFoundation → OS), NOT Unity audio,
    /// which is disabled in this Wwise game. Volume is settable for distance attenuation.
    /// </summary>
    internal sealed class AvProPlayer
    {
        private GameObject? _go;
        private MediaPlayer? _mp;

        /// <summary>True once the MediaPlayer component exists.</summary>
        public bool Exists => _mp != null;

        /// <summary>Creates the MediaPlayer once (on its own DontDestroyOnLoad object), audio via System.</summary>
        public void EnsureCreated()
        {
            if (_mp != null) return;
            _go = new GameObject("StellarAvProPlayer");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _mp = _go.AddComponent<MediaPlayer>();

            var win = _mp.PlatformOptionsWindows;
            if (win != null)
            {
                // OS audio path (DirectShow/MediaFoundation) — Unity's own audio engine is disabled here.
                win._audioMode = Windows.AudioOutput.System;
                win.useUnityAudio = false;
            }
            _mp.Loop = true;
            _mp.AudioVolume = 1f;
        }

        /// <summary>Opens a file path or direct URL and starts playback. Returns false if not created/failed.</summary>
        public bool Open(string path)
        {
            if (_mp == null) return false;
            return _mp.OpenMedia(MediaPathType.AbsolutePathOrURL, path, /*autoPlay*/ true);
        }

        /// <summary>The current decoded video frame texture, or null before the first frame is ready.</summary>
        public Texture? CurrentTexture()
        {
            var tp = _mp != null ? _mp.TextureProducer : null;
            if (tp == null || tp.GetTextureCount() < 1) return null;
            return tp.GetTexture(0);
        }

        /// <summary>Video width in pixels (0 until metadata is ready).</summary>
        public int VideoWidth => _mp != null && _mp.Info != null ? _mp.Info.GetVideoWidth() : 0;

        /// <summary>Video height in pixels (0 until metadata is ready).</summary>
        public int VideoHeight => _mp != null && _mp.Info != null ? _mp.Info.GetVideoHeight() : 0;

        /// <summary>Sets playback volume in [0,1] (distance attenuation applies this).</summary>
        public void SetVolume(float v)
        {
            if (_mp != null) _mp.AudioVolume = Mathf.Clamp01(v);
        }

        /// <summary>Stops playback and destroys the player.</summary>
        public void Destroy()
        {
            if (_mp != null) { _mp.CloseMedia(); _mp = null; }
            if (_go != null) { UnityEngine.Object.Destroy(_go); _go = null; }
        }
    }
}
