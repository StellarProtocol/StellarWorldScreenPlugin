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

        private readonly IPluginServices _services;
        private readonly IPluginLog _log;
        private readonly FrameSink _sink = new();
        private readonly HelperLauncher _launcher;
        private readonly HelperClient _client;
        private readonly WorldScreenView _screen = new();
        private readonly string _exePath = HelperLauncher.ResolveExePath();

        private Action<float>? _update;
        private bool _placed;

        public string Name => "World Screen";

        public WorldScreenPlugin(IPluginServices services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _log = services.Log;
            _launcher = new HelperLauncher(_log.Info);
            _client = new HelperClient(_sink);

            // HelperClient events fire on its background thread — marshal to Unity's main thread.
            _client.OnConnected += () => _services.Framework.Post(() => _log.Info("[WorldScreen] helper connected"));
            _client.OnDisconnected += () => _services.Framework.Post(() => _log.Warning("[WorldScreen] helper disconnected"));
            _client.OnStreamInfo += info => _services.Framework.Post(() => OnStreamInfo(info));

            _update = OnUpdate;
            _services.Framework.Update += _update;

            _log.Info($"[WorldScreen] launching helper: {_exePath}");
            _launcher.EnsureRunning(_exePath, BuildArgs(ResolveInitialSource()));
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
            _log.Info($"[WorldScreen] loading source: {sourceSpec}");
            _launcher.Restart(_exePath, BuildArgs(sourceSpec));
        }

        // Builds the helper command line for a source spec (source value quoted to tolerate spaces).
        private static string BuildArgs(string sourceSpec) => $"{ListenArg} --source \"{sourceSpec}\"";

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
            _screen.EnsureCreated(info.W, info.H);
            _screen.SetVisible(false); // stays hidden until placed in-world
        }

        // Main-thread per-frame tick.
        private void OnUpdate(float dt)
        {
            if (_sink.TryTakeLatest(out var w, out var h, out var buffer))
                _screen.Upload(buffer, w, h);

            if (!_placed && _screen.Exists && IsInWorld())
                PlaceScreen();
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
            try { _client.Dispose(); } catch (Exception) { }
            try { _launcher.Stop(); } catch (Exception) { }
            try { _screen.Destroy(); } catch (Exception) { }
        }
    }
}
