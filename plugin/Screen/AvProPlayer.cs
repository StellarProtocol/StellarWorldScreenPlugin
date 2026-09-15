using UnityEngine;
using RenderHeads.Media.AVProVideo;

namespace Stellar.WorldScreen.Screen
{
    /// <summary>
    /// Video playback via the game's own AVPro <see cref="MediaPlayer"/>: hardware decode + internal A/V sync,
    /// rendered to a Unity <see cref="Texture"/> handed to the world screen. Audio uses AVPro's
    /// <see cref="AudioOutput.System"/> path (MediaFoundation → OS), NOT Unity audio (disabled in this Wwise game).
    ///
    /// <para>Two modes: a SINGLE source (a file, the 360p combined stream) plays on one player with its own audio;
    /// a PAIR (YouTube's separate &gt;360p video-only + audio-only streams) plays the video on the main player and
    /// the audio on a second player, kept in sync here — so &gt;360p streams natively (no remux, no disk, native
    /// seek). The audio is the sync master (audio glitches are more noticeable than a video frame jump), so the
    /// video is nudged to it when they drift.</para>
    /// </summary>
    internal sealed class AvProPlayer
    {
        private const double SyncThresholdS = 0.20; // nudge the video when it drifts from the audio by more than this

        private GameObject? _go;
        private MediaPlayer? _mp;         // video (and, for a single source, its own audio)
        private GameObject? _audioGo;
        private MediaPlayer? _audioMp;    // separate audio track for a PAIR (null otherwise)
        private bool _pairMode;           // audio comes from _audioMp (direct video+audio streaming)
        private float _volume = 1f;
        private bool _seekable = true;

        /// <summary>True once the (video) MediaPlayer component exists.</summary>
        public bool Exists => _mp != null;

        /// <summary>The underlying video MediaPlayer (null until created) — for a DisplayUGUI to bind to.</summary>
        public MediaPlayer? Player => _mp;

        /// <summary>Creates the video MediaPlayer once (on its own DontDestroyOnLoad object), audio via System.</summary>
        public void EnsureCreated()
        {
            if (_mp != null) return;
            _go = new GameObject("StellarAvProPlayer");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _mp = _go.AddComponent<MediaPlayer>();
            ConfigureAudio(_mp);
            _mp.Loop = true;
            _mp.AudioVolume = 1f;
        }

        private void EnsureAudioCreated()
        {
            if (_audioMp != null) return;
            _audioGo = new GameObject("StellarAvProAudio");
            UnityEngine.Object.DontDestroyOnLoad(_audioGo);
            _audioMp = _audioGo.AddComponent<MediaPlayer>();
            ConfigureAudio(_audioMp);
            _audioMp.Loop = true;
            _audioMp.AudioVolume = 1f;
        }

        private static void ConfigureAudio(MediaPlayer mp)
        {
            var win = mp.PlatformOptionsWindows;
            if (win != null) { win._audioMode = Windows.AudioOutput.System; win.useUnityAudio = false; }
        }

        /// <summary>Opens a SINGLE file/URL (self-contained audio) and starts playback. Exits pair mode.</summary>
        public bool Open(string path)
        {
            EnsureCreated();
            if (_mp == null) return false;
            _pairMode = false;
            if (_audioMp != null) { try { _audioMp.CloseMedia(); } catch { } } // no separate audio for a single source
            _seekable = true;
            ApplyVolume();
            return _mp.OpenMedia(MediaPathType.AbsolutePathOrURL, path, /*autoPlay*/ true);
        }

        /// <summary>Opens a PAIR: video-only on the main player, audio-only on the second player, both autoplay.
        /// Both are complete indexed MP4s → native seek. Sync is maintained by <see cref="SyncTick"/>.</summary>
        public bool OpenPair(string videoUrl, string audioUrl)
        {
            EnsureCreated();
            EnsureAudioCreated();
            if (_mp == null || _audioMp == null) return false;
            _pairMode = true;
            _seekable = true;
            _mp.AudioVolume = 0f; // the video track's own audio (if any) is silenced; sound comes from _audioMp
            bool vok = _mp.OpenMedia(MediaPathType.AbsolutePathOrURL, videoUrl, /*autoPlay*/ true);
            bool aok = _audioMp.OpenMedia(MediaPathType.AbsolutePathOrURL, audioUrl, /*autoPlay*/ true);
            ApplyVolume();
            return vok && aok;
        }

        /// <summary>Per-frame (in pair mode): keep the audio playing/paused with the video and nudge the VIDEO to
        /// the audio when it drifts (audio stays the smooth master). No-op for a single source.</summary>
        public void SyncTick()
        {
            if (!_pairMode || _mp?.Control == null || _audioMp?.Control == null) return;
            bool vplaying = _mp.Control.IsPlaying();
            bool aplaying = _audioMp.Control.IsPlaying();
            if (vplaying && !aplaying) _audioMp.Control.Play();
            else if (!vplaying && aplaying) _audioMp.Control.Pause();
            if (vplaying && aplaying)
            {
                double vpos = _mp.Control.GetCurrentTime();
                double apos = _audioMp.Control.GetCurrentTime();
                if (apos > 0.1 && System.Math.Abs(vpos - apos) > SyncThresholdS) _mp.Control.Seek(apos);
            }
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
        public void SetVolume(float v) { _volume = Mathf.Clamp01(v); ApplyVolume(); }

        // Applies the current volume to whichever player carries the sound.
        private void ApplyVolume()
        {
            if (_pairMode)
            {
                if (_audioMp != null) _audioMp.AudioVolume = _volume;
                if (_mp != null) _mp.AudioVolume = 0f;
            }
            else if (_mp != null) _mp.AudioVolume = _volume;
        }

        /// <summary>Enables/disables looping (both players in pair mode).</summary>
        public void SetLoop(bool loop)
        {
            if (_mp != null) _mp.Loop = loop;
            if (_audioMp != null) _audioMp.Loop = loop;
        }

        /// <summary>Current playback volume in [0,1].</summary>
        public float Volume => _volume;

        /// <summary>True while the (video) media is actively playing.</summary>
        public bool IsPlaying => _mp != null && _mp.Control != null && _mp.Control.IsPlaying();

        /// <summary>Toggles play/pause (both players in pair mode).</summary>
        public void TogglePause()
        {
            var c = _mp != null ? _mp.Control : null;
            if (c == null) return;
            bool play = !c.IsPlaying();
            if (play) c.Play(); else c.Pause();
            var ac = _pairMode ? _audioMp?.Control : null;
            if (ac != null) { if (play) ac.Play(); else ac.Pause(); }
        }

        /// <summary>Current playback position in seconds (the video's — synced to the audio in pair mode).</summary>
        public double CurrentTime => (_mp != null && _mp.Control != null) ? _mp.Control.GetCurrentTime() : 0.0;

        /// <summary>Media duration in seconds (reported natively by the complete MP4).</summary>
        public double Duration => (_mp != null && _mp.Info != null) ? _mp.Info.GetDuration() : 0.0;

        /// <summary>Seeks to <paramref name="seconds"/> (both players in pair mode). Native — the streams are
        /// complete indexed MP4s, so MediaFoundation fetches only the bytes at the target (like YouTube).</summary>
        public void Seek(double seconds)
        {
            if (!_seekable) return;
            _mp?.Control?.Seek(seconds);
            if (_pairMode) _audioMp?.Control?.Seek(seconds);
        }

        /// <summary>Whether the current source can be sought (always true now: single files and both pair streams
        /// are complete indexed MP4s).</summary>
        public bool IsSeekable => _seekable;

        /// <summary>Marks the current source seekable or not.</summary>
        public void SetSeekable(bool seekable) => _seekable = seekable;

        /// <summary>True while audio is muted (the audio player in pair mode).</summary>
        public bool IsMuted
        {
            get { var c = (_pairMode ? _audioMp : _mp)?.Control; return c != null && c.IsMuted(); }
        }

        /// <summary>Toggles mute on whichever player carries the sound.</summary>
        public void ToggleMute() { var c = (_pairMode ? _audioMp : _mp)?.Control; if (c != null) c.MuteAudio(!c.IsMuted()); }

        /// <summary>Stops playback and destroys both players.</summary>
        public void Destroy()
        {
            if (_mp != null) { _mp.CloseMedia(); _mp = null; }
            if (_go != null) { UnityEngine.Object.Destroy(_go); _go = null; }
            if (_audioMp != null) { _audioMp.CloseMedia(); _audioMp = null; }
            if (_audioGo != null) { UnityEngine.Object.Destroy(_audioGo); _audioGo = null; }
            _pairMode = false;
        }
    }
}
