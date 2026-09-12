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
    public sealed class WorldScreenPlugin : IStellarPlugin
    {
        private const string HelperHost = "127.0.0.1";
        private const int HelperPort = 47800;
        private const string ListenArg = "--listen 127.0.0.1:47800";

        // Distance-based audio: the helper (ffplay) plays the sound; the plugin sends it a 0–100 volume from
        // the player's in-world distance to the screen. Full within MinAudioDist, silent past MaxAudioDist.
        private const float MinAudioDist = 3f;
        private const float MaxAudioDist = 40f;

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

        // Render-quality presets (label, width, height); the index is the overlay dropdown selection.
        private static readonly (string Label, int W, int H)[] Qualities =
        {
            ("360p", 640, 360),
            ("480p", 854, 480),
            ("720p", 1280, 720),
        };
        private int _quality;                          // index into Qualities (0 = 360p default)
        private string _currentSource = "testpattern"; // last-loaded source, so a quality change reloads it

        public string Name => "World Screen";

        public WorldScreenPlugin(IPluginServices services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _log = services.Log;
            _launcher = new HelperLauncher(_log.Info);
            _client = new HelperClient(_sink);
            _overlay = new UI.OverlayPanel(services, LoadSource, HandleControl, QualityLabels(), () => _quality, SetQuality);

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
            _currentSource = sourceSpec;
            _log.Info($"[WorldScreen] loading source: {RedactSource(sourceSpec)}");
            _overlay.SetStatus("Loading…");
            _lastVolume = -1; // force a volume resend once the relaunched helper reconnects
            _launcher.Restart(_exePath, BuildArgs(sourceSpec));
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
            _log.Info($"[WorldScreen] quality -> {Qualities[index].Label}");
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
            if (_sink.TryTakeLatest(out var w, out var h, out var buffer))
                _screen.Upload(buffer, w, h);

            if (!_placed && _screen.Exists && IsInWorld())
                PlaceScreen();

            if (_placed)
                PumpVolume(dt);
        }

        // Distance-based volume: a few times a second, measure the player's (camera's) distance to the screen,
        // map it to 0–100 (full within MinAudioDist, linear fade to silent past MaxAudioDist), and send it to
        // the helper only when it changes. The helper applies it as a gain to the audio it plays via ffplay.
        private void PumpVolume(float dt)
        {
            _volTimer += dt;
            if (_volTimer < 0.2f) return;
            _volTimer = 0f;

            var root = _screen.Root;
            if (root == null) return;

            var cam = GetActiveCamera();
            Vector3 listener;
            if (cam != null)
            {
                listener = cam.transform.position;
            }
            else
            {
                var p = _services.PlayerState.Position;
                listener = new Vector3(p.X, p.Y + 1.6f, p.Z);
            }

            float dist = Vector3.Distance(listener, root.position);
            float t = Mathf.Clamp01((MaxAudioDist - dist) / (MaxAudioDist - MinAudioDist));
            int vol = Mathf.RoundToInt(t * 100f);
            if (vol == _lastVolume) return;
            _lastVolume = vol;
            _client.Send(Net.ControlOp.Volume, volume: (byte)vol);
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
            try { _overlay.Remove(); } catch (Exception) { }
            try { _client.Dispose(); } catch (Exception) { }
            try { _launcher.Stop(); } catch (Exception) { }
            try { _screen.Destroy(); } catch (Exception) { }
        }
    }
}
