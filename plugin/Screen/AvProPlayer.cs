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
        private double _durationOverrideS; // known duration for sources AVPro reports 0 for (live HLS mux)
        private double _positionOffsetS;   // added to CurrentTime — the video time the current stream STARTED at
                                           // (a seek re-streams from T, so the stream plays from 0 but is at T+local)
        private bool _seekable = true;     // in-place seek OK (direct file/360p); a mux seeks by RE-STREAMING (plugin)

        /// <summary>True once the MediaPlayer component exists.</summary>
        public bool Exists => _mp != null;

        /// <summary>The underlying AVPro MediaPlayer (null until created) — for a DisplayUGUI to bind to.</summary>
        public MediaPlayer? Player => _mp;

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

        /// <summary>Enables/disables looping. Single-source portals loop; a multi-item playlist turns
        /// looping OFF so the video ENDS (letting the DJ auto-advance to the next item) instead of repeating.</summary>
        public void SetLoop(bool loop)
        {
            if (_mp != null) _mp.Loop = loop;
        }

        /// <summary>Current playback volume in [0,1].</summary>
        public float Volume => (_mp != null && _mp.Control != null) ? _mp.Control.GetVolume() : 1f;

        /// <summary>True while the media is actively playing (false when paused/stopped/not ready).</summary>
        public bool IsPlaying => _mp != null && _mp.Control != null && _mp.Control.IsPlaying();

        /// <summary>Toggles play/pause.</summary>
        public void TogglePause()
        {
            var c = _mp != null ? _mp.Control : null;
            if (c == null) return;
            if (c.IsPlaying()) c.Pause(); else c.Play();
        }

        /// <summary>Current playback position in seconds — the TRUE video time, i.e. the current stream's local
        /// time plus the offset it started at (a seek re-streams from T, so local time is 0-based from T).</summary>
        public double CurrentTime => _positionOffsetS + ((_mp != null && _mp.Control != null) ? _mp.Control.GetCurrentTime() : 0.0);

        /// <summary>Sets the video time the current stream starts at (for a re-streamed seek); 0 for a normal load.</summary>
        public void SetPositionOffset(double seconds) => _positionOffsetS = seconds > 0 ? seconds : 0.0;

        /// <summary>Media duration in seconds. When a known duration is supplied (a live progressive mux, for
        /// which AVPro reports only the loaded fragment — e.g. ~5s — not the whole video), that value WINS; the
        /// override is only set for resolved streams (from yt-dlp) and cleared for local/direct sources, where
        /// AVPro's own report is authoritative.</summary>
        public double Duration
        {
            get
            {
                if (_durationOverrideS > 0.01) return _durationOverrideS;
                return (_mp != null && _mp.Info != null) ? _mp.Info.GetDuration() : 0.0;
            }
        }

        /// <summary>Sets a fallback duration (seconds) used when AVPro itself reports 0 (live HLS). 0 clears it.</summary>
        public void SetDurationOverride(double seconds) => _durationOverrideS = seconds > 0 ? seconds : 0.0;

        /// <summary>Seeks to <paramref name="seconds"/> — ignored when the source is not seekable (a live
        /// progressive mux has no seek index, and seeking it freezes MediaFoundation).</summary>
        public void Seek(double seconds) { if (_seekable) _mp?.Control?.Seek(seconds); }

        /// <summary>Whether the current source can be sought (false for the live progressive-mux stream).</summary>
        public bool IsSeekable => _seekable;

        /// <summary>Marks the current source seekable or not — the UI hides/ignores the seek knob when false.</summary>
        public void SetSeekable(bool seekable) => _seekable = seekable;

        /// <summary>True while audio is muted.</summary>
        public bool IsMuted => _mp != null && _mp.Control != null && _mp.Control.IsMuted();

        /// <summary>Toggles mute.</summary>
        public void ToggleMute() { var c = _mp?.Control; if (c != null) c.MuteAudio(!c.IsMuted()); }

        /// <summary>Stops playback and destroys the player.</summary>
        public void Destroy()
        {
            if (_mp != null) { _mp.CloseMedia(); _mp = null; }
            if (_go != null) { UnityEngine.Object.Destroy(_go); _go = null; }
        }
    }
}
