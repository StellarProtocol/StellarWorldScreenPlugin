using System;
using UnityEngine;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;
using Stellar.WorldScreen.Net;
using FrameSink = Stellar.WorldScreen.Screen.FrameSink;
using WorldScreenView = Stellar.WorldScreen.Screen.WorldScreen;

namespace Stellar.WorldScreen
{
    /// <summary>
    /// Composition root for the World Screen plugin. Auto-launches the bundled <c>stellar-castbox.exe</c>,
    /// connects to it over localhost TCP, and paints the frames it streams onto a world-space screen placed
    /// in front of the player. Milestone-A scope: test-pattern source, auto-placed screen, no overlay yet.
    /// </summary>
    public sealed partial class WorldScreenPlugin : IStellarPlugin
    {
        private const string HelperHost = "127.0.0.1";
        private const int HelperPort = 47800;
        private const string ListenArg = "--listen 127.0.0.1:47800";

        // Distance-based audio: full within MinAudioDist, silent past MaxAudioDist.
        private const float MinAudioDist = 3f;
        private const float MaxAudioDist = 40f;

        // Playback runs through the game's AVPro engine (HW decode + internal A/V sync) instead of the
        // raw-frame wire pipeline. The helper is not launched; yt-dlp resolves page URLs to a direct URL
        // AVPro can open. Kept as a flag so the old wire path can be restored by flipping it to false.
        private const bool UseAvPro = true;
        private const float InteractRadius = 12f; // show the action menu when the player is within this many metres
        private readonly Screen.AvProPlayer _avpro = new();
        private readonly Screen.FullscreenView _fullscreen = new();
        private readonly Net.YtDlpResolver _resolver;
        private readonly Net.StreamMuxer _muxer;
        private UI.ActionMenu? _actionMenu;
        private bool _avproInitDone;
        private bool _playerNear;

        private readonly IPluginServices _services;
        private readonly IPluginLog _log;
        private readonly FrameSink _sink = new();
        private readonly HelperLauncher _launcher;
        private readonly HelperClient _client;
        private readonly WorldScreenView _screen = new();
        private readonly string _exePath = HelperLauncher.ResolveExePath();
        private readonly UI.OverlayPanel _overlay;

        private Action<float>? _update;
        private bool _placed;
        private float _volTimer;
        private int _lastVolume = -1; // last volume sent to the helper (−1 = none yet), so we only send on change
        private double _resumeSeekS = -1.0; // after a same-source reload (quality change), resume here instead of 0:00
        private string _avOpenSource = "";  // the source spec AVPro actually has OPEN (set at each real open) — lets
                                            // DJ logic tell the PLAYING item apart from the still-loading/test-pattern
                                            // clip (VideoWidth>0 is true for the test pattern too, so it can't be used)

        // Pending mux-stream open: AVPro opens the HLS playlist only once ffmpeg has written a segment
        // (PlaylistReady). Set when a merge stream is muxing; cleared once frames flow (or superseded / fell
        // back). See UpdateAvPro / OpenMuxStream.
        private const float MuxReadyTimeoutS = 15f;  // give up if ffmpeg produces no HLS segment within this
        private const float MuxRetryEveryS = 3.0f;   // re-open interval while MediaFoundation isn't yet playing
        private const int MuxMaxAttempts = 5;        // then fall back to a combined (lower-quality) stream
        private string? _pendingMuxUrl;              // the http://127.0.0.1:PORT/stream.m3u8 AVPro should open
        private string? _pendingMuxSource;           // the source spec this mux belongs to (supersede guard)
        private float _muxTimer;                     // seconds since the mux started / since the last open attempt
        private int _muxAttempts;                    // AVPro open attempts made so far
        private bool _muxFellBack;                   // already fell back to combined for this source (once only)

        // Render-quality presets (label, width, height); the index is the overlay dropdown selection.
        private static readonly (string Label, int W, int H)[] Qualities =
        {
            ("360p", 640, 360),
            ("480p", 854, 480),
            ("720p", 1280, 720),
            ("1080p", 1920, 1080),
        };
        private int _quality = 3;                       // index into Qualities (default 1080p — best quality out of the box)
        private string _currentSource = "testpattern"; // last-loaded source, so a quality change reloads it

        public string Name => "World Screen";

        public WorldScreenPlugin(IPluginServices services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _log = services.Log;
            _launcher = new HelperLauncher(_log.Info);
            _client = new HelperClient(_sink);
            var slotDir = System.IO.Path.GetDirectoryName(_exePath) ?? ".";
            _resolver = new Net.YtDlpResolver(
                System.IO.Path.Combine(slotDir, "yt-dlp.exe"), slotDir,
                jsRuntimePath: System.IO.Path.Combine(slotDir, "deno.exe")); // supported path → 1080p+
            _muxer = new Net.StreamMuxer(System.IO.Path.Combine(slotDir, "ffmpeg.exe"), slotDir);
            _overlay = new UI.OverlayPanel(services, LoadSource, HandleControl, QualityLabels(), () => _quality, SetQuality,
                shouldRender: () => !_fullscreen.Visible,
                onPlacePortal: PlacePortalHere, onRemovePortal: RemoveMyPortal, portalStatus: PortalStatus,
                onAddToPlaylist: AddToPlaylist, onClearPlaylist: ClearDraftPlaylist, playlistStatus: PlaylistStatus);
            _actionMenu = new UI.ActionMenu(
                services,
                // Proximity menu only — full-screen has its own on-canvas control bar above the video.
                shouldRender: () => _playerNear && !_fullscreen.Visible && _avpro.Exists,
                isFullscreen: () => _fullscreen.Visible,
                toggleFullscreen: ToggleFullscreen,
                isPlaying: () => _avpro.IsPlaying,
                togglePause: () => _avpro.TogglePause(),
                stop: () => LoadSource("testpattern"),
                onCmd: HandleControl, qualityLabels: QualityLabels(), currentQuality: () => _quality, onQuality: SetQuality,
                isAutoFace: () => ScreenAutoFace, toggleAutoFace: ToggleScreenAutoFace);
            _fullscreen.Bind(_avpro, onStop: () => LoadSource("testpattern"));

            // HelperClient events fire on its background thread — marshal to Unity's main thread.
            _client.OnConnected += () => _services.Framework.Post(() =>
            {
                _log.Info("[WorldScreen] helper connected");
                _overlay.SetStatus("Helper: connected");
            });
            _client.OnDisconnected += () => _services.Framework.Post(() =>
            {
                _log.Warning("[WorldScreen] helper disconnected");
                _overlay.SetStatus("Helper: reconnecting…");
            });
            _client.OnStreamInfo += info => _services.Framework.Post(() => OnStreamInfo(info));

            _update = OnUpdate;
            _services.Framework.Update += _update;

            // SP-1c: shared-portal backend wiring (independent of the video-source mode below).
            InitPortal();

            if (UseAvPro)
            {
                _log.Info("[WorldScreen] AVPro spike mode — helper NOT launched; playing bundled test clip");
                _overlay.SetStatus("AVPro: starting…");
                return;
            }

            var initialSource = ResolveInitialSource();
            _currentSource = initialSource;
            _log.Info($"[WorldScreen] launching helper: {_exePath} (source: {RedactSource(initialSource)})");
            _launcher.EnsureRunning(_exePath, BuildArgs(initialSource));
            _client.Start(HelperHost, HelperPort);
        }

        /// <summary>
        /// Loads a new source at runtime by relaunching the helper with it. <paramref name="sourceSpec"/> is
        /// "testpattern", "file:&lt;path&gt;", or "url:&lt;url&gt;". The screen keeps its placement; the client
        /// auto-reconnects to the relaunched helper and the new STREAM_INFO/frames take over.
        /// </summary>
        public void LoadSource(string sourceSpec)
        {
            if (string.IsNullOrWhiteSpace(sourceSpec)) return;
            if (sourceSpec != _currentSource) _resumeSeekS = -1.0; // a NEW video starts at 0; a same-source reload (quality) resumes
            _currentSource = sourceSpec;
            _log.Info($"[WorldScreen] loading source: {RedactSource(sourceSpec)}");
            _overlay.SetStatus("Loading…");
            _lastVolume = -1; // force a volume re-apply for the new source
            _muxer.Stop(); _pendingMuxUrl = null; _muxFellBack = false; // tear down any prior live mux
            _avpro.SetDurationOverride(0); // clear any previous known-duration; the resolve sets the new one

            if (!UseAvPro)
            {
                _launcher.Restart(_exePath, BuildArgs(sourceSpec));
                return;
            }

            // AVPro: open a file/direct-URL immediately; resolve a page URL (YouTube) via yt-dlp first.
            if (sourceSpec.StartsWith("file:", StringComparison.Ordinal)) { OpenAvPro(sourceSpec.Substring(5)); return; }
            if (sourceSpec == "testpattern") { OpenAvPro(TestClipPath()); return; }
            if (sourceSpec.StartsWith("url:", StringComparison.Ordinal))
            {
                var url = sourceSpec.Substring(4);
                if (IsDirectMedia(url)) { OpenAvPro(url); return; }
                if (!_resolver.Available) { _overlay.SetStatus("yt-dlp missing"); return; }
                // STREAM, don't download: resolve the page URL to DIRECT stream URL(s), capped at the chosen
                // quality height. ONE url = a combined format (open directly); TWO = separate video+audio (the
                // only way YouTube serves >360p) → ffmpeg live-muxes them into one MP4 AVPro streams. Each client
                // resolves the shared page URL itself; the temporary direct URLs are never stored.
                _overlay.SetStatus("Resolving stream…");
                _log.Info($"[WorldScreen] resolving stream: {RedactSource(sourceSpec)}");
                var selector = Net.YtDlpResolver.MergeSelectorForHeight(Qualities[_quality].H);
                _resolver.ResolveMergeUrlsAsync(url, selector, res => _services.Framework.Post(() =>
                {
                    if (sourceSpec != _currentSource) return; // a newer Load superseded this one
                    if (res == null || res.Urls.Length == 0)
                    {
                        _overlay.SetStatus("Stream resolve failed");
                        _log.Warning("[WorldScreen] stream resolve FAILED — yt-dlp -g returned nothing (old yt-dlp? blocked? unavailable video)");
                        return;
                    }
                    _avpro.SetDurationOverride(res.DurationSec); // HLS carries no duration → supply the known one
                    if (res.Urls.Length == 1) // combined format (360p / direct progressive) — no mux needed
                    {
                        _log.Info("[WorldScreen] stream resolved (combined) → opening (streaming, no download)");
                        OpenAvPro(res.Urls[0]);
                        return;
                    }
                    // Two URLs (video-only + audio-only): mux live via ffmpeg → localhost HTTP → AVPro.
                    var httpUrl = _muxer.StartServe(res.Urls[0], res.Urls[1]);
                    if (string.IsNullOrEmpty(httpUrl))
                    {
                        _overlay.SetStatus("Mux start failed");
                        _log.Warning("[WorldScreen] ffmpeg mux failed to start — is ffmpeg.exe present?");
                        return;
                    }
                    _log.Info($"[WorldScreen] muxing {Qualities[_quality].H}p stream via ffmpeg → {httpUrl} (opening shortly)");
                    _overlay.SetStatus($"Buffering {Qualities[_quality].Label}…");
                    _pendingMuxUrl = httpUrl;
                    _pendingMuxSource = sourceSpec;
                    _muxTimer = 0f;
                    _muxAttempts = 0;
                }));
            }
        }

        // Drives the AVPro open for a live HLS mux stream (called each frame from UpdateAvPro while
        // _pendingMuxUrl is set): wait until ffmpeg has written the first segment (PlaylistReady), open the
        // playlist, re-open a few times if MediaFoundation hasn't started, then fall back to a combined stream.
        private void OpenMuxStream(float dt)
        {
            if (_pendingMuxUrl == null) return;
            if (_pendingMuxSource != _currentSource) { _pendingMuxUrl = null; return; } // superseded
            // Playing only once the MUX stream itself is the open source. VideoWidth>0 alone is fooled by the
            // still-showing test pattern, so also require _avOpenSource to be this mux.
            if (_avOpenSource == _pendingMuxSource && _avpro.VideoWidth > 0) { _pendingMuxUrl = null; return; }

            _muxTimer += dt;

            // Phase 1 — not opened yet: wait for the HLS playlist to have a segment, then open it once.
            if (_avOpenSource != _pendingMuxSource)
            {
                if (_muxer.PlaylistReady) { _muxTimer = 0f; _muxAttempts = 1; OpenAvPro(_pendingMuxUrl); }
                else if (_muxTimer > MuxReadyTimeoutS) MuxGiveUp(); // ffmpeg produced no segment → give up
                return;
            }

            // Phase 2 — opened but no frames yet: give MediaFoundation time, re-open a few times (the playlist
            // has more segments now), then fall back to a combined stream that plays for sure.
            if (_muxTimer < MuxRetryEveryS) return;
            _muxTimer = 0f;
            if (_muxAttempts >= MuxMaxAttempts) { MuxGiveUp(); return; }
            _muxAttempts++;
            OpenAvPro(_pendingMuxUrl);
        }

        // The HLS mux never became playable — stop it and fall back ONCE to a combined (single-URL, usually
        // 360p) stream, which MediaFoundation plays reliably. Better a lower-quality video than a blank screen.
        private void MuxGiveUp()
        {
            var spec = _pendingMuxSource;
            _pendingMuxUrl = null;
            _muxer.Stop();
            if (spec != null && spec == _currentSource && !_muxFellBack)
            {
                _muxFellBack = true;
                _log.Warning("[WorldScreen] mux stream not playable — falling back to a combined (lower-quality) stream");
                FallbackToCombined(spec);
            }
            else { _overlay.SetStatus("Stream open failed"); _log.Warning("[WorldScreen] mux stream not playable — giving up"); }
        }

        // Resolves the page URL to a single COMBINED format (no mux) and opens it directly — the reliable
        // lower-quality path when the HLS mux won't play.
        private void FallbackToCombined(string sourceSpec)
        {
            if (!sourceSpec.StartsWith("url:", StringComparison.Ordinal)) { _overlay.SetStatus("Stream failed"); return; }
            var url = sourceSpec.Substring(4);
            _overlay.SetStatus("Buffering (lower quality)…");
            _resolver.ResolveMergeUrlsAsync(url, Net.YtDlpResolver.BestStreamableSelector, res => _services.Framework.Post(() =>
            {
                if (sourceSpec != _currentSource) return; // superseded
                if (res == null || res.Urls.Length == 0) { _overlay.SetStatus("Stream failed"); return; }
                _avpro.SetDurationOverride(res.DurationSec);
                _log.Info("[WorldScreen] combined fallback resolved → opening (direct, lower quality)");
                OpenAvPro(res.Urls[0]);
            }));
        }

        // Opens a file path or direct URL in AVPro (main thread) and reports status.
        private void OpenAvPro(string pathOrUrl)
        {
            _avpro.EnsureCreated();
            var ok = _avpro.Open(pathOrUrl);
            _avOpenSource = _currentSource; // record WHAT is now open (the intended spec), not the resolved url
            _log.Info($"[WorldScreen] AVPro Open -> {ok}");
            _overlay.SetStatus(ok ? "Playing…" : "Open failed");
        }

        // A URL is "direct media" if its path ends in a container extension AVPro can open itself; otherwise
        // it's a page URL (YouTube etc.) that yt-dlp must resolve to a direct stream first.
        private static bool IsDirectMedia(string url)
        {
            var path = url.Split('?', '#')[0].ToLowerInvariant();
            return path.EndsWith(".mp4") || path.EndsWith(".webm") || path.EndsWith(".mkv")
                || path.EndsWith(".mov") || path.EndsWith(".m3u8") || path.EndsWith(".mpd") || path.EndsWith(".avi");
        }

        // The bundled silent placeholder clip, shown until a real source is loaded.
        private string TestClipPath()
        {
            var dir = System.IO.Path.GetDirectoryName(_exePath) ?? ".";
            return System.IO.Path.Combine(dir, "test-clip.mp4");
        }

        /// <summary>Handles an overlay placement command (fires on the Unity main thread from a button).</summary>
        public void HandleControl(string cmd)
        {
            switch (cmd)
            {
                case "up": _screen.MoveVertical(0.5f); break;
                case "down": _screen.MoveVertical(-0.5f); break;
                case "nearer": _screen.MoveDepth(-1f); break;
                case "farther": _screen.MoveDepth(1f); break;
                case "bigger": _screen.ScaleBy(1.25f); break;
                case "smaller": _screen.ScaleBy(0.8f); break;
                case "replace": PlaceScreen(); break;
            }
        }

        // Builds the helper command line for a source spec at the current quality (source value quoted).
        private string BuildArgs(string sourceSpec)
        {
            var q = Qualities[_quality];
            return $"{ListenArg} --width {q.W} --height {q.H} --source \"{sourceSpec}\"";
        }

        /// <summary>Overlay dropdown: switch render quality and reload the current source at that resolution.</summary>
        public void SetQuality(int index)
        {
            if (index < 0 || index >= Qualities.Length || index == _quality) return;
            _quality = index;
            SaveScreenPrefs(); // persist the chosen quality across client restarts
            _log.Info($"[WorldScreen] quality -> {Qualities[index].Label}");
            _resumeSeekS = _avpro.CurrentTime; // resume at the current position after the reload (don't restart at 0:00)
            LoadSource(_currentSource);
        }

        /// <summary>Quality dropdown labels + current index, for the overlay.</summary>
        public static string[] QualityLabels() => System.Array.ConvertAll(Qualities, q => q.Label);
        public int Quality => _quality;

        // Redacts a source for logging: a url: source can carry an auth token in its query/path, so we
        // log only scheme+host (mirrors the helper's scheme_and_host). file:/testpattern log as-is.
        private static string RedactSource(string spec)
        {
            if (!spec.StartsWith("url:", StringComparison.Ordinal)) return spec;
            var u = spec.Substring(4);
            var i = u.IndexOf("://", StringComparison.Ordinal);
            if (i <= 0) return "url:<url>";
            var afterScheme = u.Substring(i + 3);
            var host = afterScheme.Split('/', '?', '#')[0];
            return $"url:{u.Substring(0, i)}://{host}";
        }

        // Default source: a bundled demo clip (test-clip.mp4) next to the helper if present, else the pattern.
        private string ResolveInitialSource()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_exePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    var clip = System.IO.Path.Combine(dir, "test-clip.mp4");
                    if (System.IO.File.Exists(clip)) return "file:" + clip;
                }
            }
            catch (Exception) { }
            return "testpattern";
        }

        // Main thread (posted).
        private void OnStreamInfo(StreamInfoMsg info)
        {
            _log.Info($"[WorldScreen] STREAM_INFO {info.W}x{info.H} pixfmt={info.Pixfmt} fps={info.Fps} '{info.Title}'");
            _overlay.SetStatus($"Playing: {info.Title} ({info.W}x{info.H})");
            _screen.EnsureCreated(info.W, info.H);
            if (!_placed) _screen.SetVisible(false); // stays hidden until placed in-world (first stream only)
        }

        // Main-thread per-frame tick.
        private void OnUpdate(float dt)
        {
            SnapshotPortalHeartbeat(dt); // main-thread snapshot for the portal heartbeat loop (SP-1c)

            if (UseAvPro) { UpdateAvPro(dt); return; }

            if (_sink.TryTakeLatest(out var w, out var h, out var buffer))
                _screen.Upload(buffer, w, h);

            if (!_placed && _screen.Exists && IsInWorld())
                PlaceScreen();

            if (_placed)
                PumpVolume(dt);
        }

        // AVPro playback tick: load the initial (placeholder) source once, then each frame show AVPro's
        // decoded texture on the world screen and drive distance volume. AVPro handles A/V sync internally.
        private void UpdateAvPro(float dt)
        {
            if (!_avproInitDone)
            {
                _avproInitDone = true;
                LoadSource(ResolveInitialSource()); // preload the silent clip; screen stays HIDDEN until a portal is activated
                return;
            }

            if (_pendingMuxUrl != null) OpenMuxStream(dt); // delayed/retried connect to a live ffmpeg mux stream

            // Bind AVPro's decoded output to the screen once frames are flowing (skipped harmlessly until then).
            var tex = _avpro.CurrentTexture();
            if (tex != null)
            {
                int w = _avpro.VideoWidth, h = _avpro.VideoHeight;
                if (w <= 0 || h <= 0) { w = tex.width; h = tex.height; }
                if (w > 0 && h > 0) _screen.ShowVideoPlayer(_avpro.Player, w, h); // AVPro DisplayUGUI (correct colour)
            }

            // After a same-source reload (quality change), jump back to where we were instead of restarting at
            // 0:00 — once the NEW media has loaded (Duration ready) and is playing below the resume point.
            if (_resumeSeekS > 1.0 && _avpro.Duration > 1.0 && _avpro.CurrentTime < _resumeSeekS - 1.0)
            {
                _avpro.Seek(_resumeSeekS);
                _resumeSeekS = -1.0;
            }

            // Portal walk-up activation drives whether/where the screen shows (SP-1c): the screen appears ONLY
            // when the player activates a nearby portal — there is no auto-placed personal screen. This MUST run
            // every frame (never gated behind AVPro frame readiness) or an F press on a not-ready frame is dropped.
            UpdatePortalActivation(dt);

            if (_fullscreen.Visible)
            {
                _fullscreen.Tick(dt);
                if (Input.GetKeyDown(KeyCode.Escape)) _fullscreen.Hide(); // always-available exit fallback
            }
            else if (_activePortalId != null)
            {
                PumpVolume(dt); // distance volume relative to the activated beacon (the screen sits on it)
            }
        }

        /// <summary>Action-menu button: toggle the full-screen "cinema" overlay of the current video.</summary>
        private void ToggleFullscreen()
        {
            _fullscreen.Toggle();
            if (_fullscreen.Visible)
            {
                _avpro.SetVolume(1f); // full volume while watching in full-screen (bar has a mute toggle)
                _lastVolume = -1;     // force distance-volume to re-apply after exit
            }
        }

        // Horizontal distance from the PLAYER (not the camera — it can be far in a zoomed-out third-person
        // view) to the screen, or -1 if the screen isn't up. This is the "how near is the user" metric used
        // for both the proximity action menu and the volume falloff.
        private float ScreenDistance()
        {
            var root = _screen.Root;
            if (root == null) return -1f;
            var p = _services.PlayerState.Position;
            var d = root.position - new Vector3(p.X, p.Y, p.Z);
            d.y = 0f; // the screen sits above ground; compare on the ground plane
            return d.magnitude;
        }

        // Distance-based volume: a few times a second map the screen distance to 0–100 (full within
        // MinAudioDist, linear fade to silent past MaxAudioDist) and apply it, only when it changes.
        private void PumpVolume(float dt)
        {
            _volTimer += dt;
            if (_volTimer < 0.2f) return;
            _volTimer = 0f;

            float dist = ScreenDistance();
            if (dist < 0f) return;
            float t = Mathf.Clamp01((MaxAudioDist - dist) / (MaxAudioDist - MinAudioDist));
            int vol = Mathf.RoundToInt(t * 100f);
            if (vol == _lastVolume) return;
            _lastVolume = vol;
            if (UseAvPro) _avpro.SetVolume(vol / 100f);        // AVPro plays the audio (System output)
            else _client.Send(Net.ControlOp.Volume, volume: (byte)vol); // helper (ffplay) applies the gain
        }

        private void PlaceScreen()
        {
            var cam = GetActiveCamera();
            Vector3 origin;
            Vector3 forward;
            if (cam != null)
            {
                origin = cam.transform.position;
                forward = cam.transform.forward;
            }
            else
            {
                var p = _services.PlayerState.Position;
                origin = new Vector3(p.X, p.Y + 1.6f, p.Z);
                forward = Vector3.forward;
            }

            _screen.PlaceInFrontOf(origin, forward);
            _screen.SetVisible(true);
            _placed = true;
            _log.Info($"[WorldScreen] placed screen (camera={cam != null}) at {origin}");
        }

        private bool IsInWorld()
        {
            var p = _services.PlayerState.Position;
            return (p.X * p.X + p.Z * p.Z) > 1f; // non-origin position => actually in a world scene
        }

        private static Camera? GetActiveCamera()
        {
            var cam = Camera.main;
            if (cam != null) return cam;
            cam = Camera.current;
            if (cam != null) return cam;
            return Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null;
        }

        public void Dispose()
        {
            if (_update != null) { _services.Framework.Update -= _update; _update = null; }
            try { DisposePortal(); } catch (Exception) { }
            try { _overlay.Remove(); } catch (Exception) { }
            try { _actionMenu?.Remove(); } catch (Exception) { }
            try { _fullscreen.Destroy(); } catch (Exception) { }
            try { _avpro.Destroy(); } catch (Exception) { }
            try { _muxer.Stop(); } catch (Exception) { }
            try { _client.Dispose(); } catch (Exception) { }
            try { _launcher.Stop(); } catch (Exception) { }
            try { _screen.Destroy(); } catch (Exception) { }
        }
    }
}
