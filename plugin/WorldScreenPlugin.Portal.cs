using System;
using System.Net.Http;
using Stellar.Abstractions.Services;
using Stellar.WorldScreen.Identity;
using Stellar.WorldScreen.Net;
using Stellar.WorldScreen.World;

namespace Stellar.WorldScreen
{
    /// <summary>
    /// SP-1c composition-root wiring for the SHARED portal backend (see
    /// <c>docs/superpowers/plans/2026-09-13-world-portal-sp1c-plugin.md</c> Task 6). When a backend URL is
    /// configured, this constructs the per-install <see cref="InstallKey"/>, a <see cref="PortalClient"/>,
    /// and starts its ~5s heartbeat loop; each heartbeat reports this client's presence + gets back the
    /// portals in its resolved instance. This increment (Phase B step 1) LOGS the result — the visible
    /// <c>PortalWorld</c> rendering of those portals is the next in-game increment.
    ///
    /// <para><b>THREADING (load-bearing):</b> <see cref="PortalClient.Start"/> invokes its <c>gather</c> on a
    /// background thread, but <see cref="InstanceProbe.Gather"/> reads main-thread-only IL2CPP state. So the
    /// heartbeat body is SNAPSHOT on the main thread here (<see cref="SnapshotPortalHeartbeat"/>, called from
    /// <c>OnUpdate</c>) into a <c>volatile</c> field, and the loop's <c>gather</c> just returns that snapshot —
    /// never touches game state off-thread. (Results arrive back via <c>Framework.Post</c>, so
    /// <see cref="OnPortalHeartbeat"/> runs on the main thread.) See memory <c>il2cpp-live-object-probing-safety</c>.</para>
    /// </summary>
    public sealed partial class WorldScreenPlugin
    {
        // Config: section "worldportal", key "backendUrl". Default points at the dev backend (Wine shares
        // the host loopback, so 127.0.0.1 reaches a backend running on the host). Empty ⇒ portals OFF and the
        // plugin behaves exactly as the standalone video screen.
        private const string PortalConfigSection = "worldportal";
        private const string PortalBackendUrlKey = "backendUrl";
        private const string DefaultPortalBackendUrl = "http://127.0.0.1:8790";

        // Re-snapshot the (main-thread) heartbeat body at most this often — the loop only sends every ~5s,
        // so snapshotting faster is wasted work (InstanceProbe.Gather enumerates AOI players each call).
        private const float PortalSnapshotIntervalS = 1f;

        private InstallKey? _installKey;
        private HttpClient? _portalHttp;
        private PortalClient? _portal;
        private IDisposable? _portalLoop;
        private float _portalSnapTimer;

        // Main-thread snapshot the background loop reads. volatile for cross-thread visibility.
        private volatile HeartbeatBody? _latestHeartbeat;

        /// <summary>Constructs the portal client + starts its heartbeat loop when a backend URL is configured.
        /// Fully guarded: any failure here logs and leaves the standalone video screen fully working.</summary>
        private void InitPortal()
        {
            try
            {
                var prefs = _services.Config.GetSection(PortalConfigSection);
                var baseUrl = prefs.Get<string>(PortalBackendUrlKey, DefaultPortalBackendUrl);
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    _log.Info("[WorldPortal] no backendUrl configured — shared portals OFF");
                    return;
                }

                _installKey = InstallKey.LoadOrCreate(
                    getPref: k => prefs.Get<string>(k, null),
                    setPref: (k, v) => { prefs.Set(k, v); prefs.Save(); });

                _portalHttp = new HttpClient();
                _portal = new PortalClient(
                    http: _portalHttp,
                    baseUrl: baseUrl!,
                    key: _installKey,
                    nowMs: () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    post: a => _services.Framework.Post(a),
                    log: msg => _log.Warning("[WorldPortal] " + msg));

                _portalLoop = _portal.Start(gather: () => _latestHeartbeat, onResult: OnPortalHeartbeat);
                _log.Info($"[WorldPortal] heartbeat loop started → {baseUrl}");
            }
            catch (Exception ex)
            {
                _log.Warning("[WorldPortal] init failed (portals disabled, video screen unaffected): " + ex.Message);
            }
        }

        /// <summary>Main-thread: refresh <see cref="_latestHeartbeat"/> from live game state, throttled. Called
        /// from <c>OnUpdate</c>. Null when not in-world — the loop then skips that tick.</summary>
        private void SnapshotPortalHeartbeat(float dt)
        {
            if (_portal == null) return;
            _portalSnapTimer += dt;
            if (_portalSnapTimer < PortalSnapshotIntervalS) return;
            _portalSnapTimer = 0f;
            _latestHeartbeat = InstanceProbe.Gather(_services); // MAIN THREAD read (see class doc)
        }

        /// <summary>Main-thread (via <c>post</c>): a heartbeat result arrived. Increment 1 logs it as
        /// owner-checkable proof the in-game plugin reached the backend + resolved its instance; the visible
        /// PortalWorld rendering of <c>result.Portals</c> is the next increment.</summary>
        private void OnPortalHeartbeat(HeartbeatResult result)
        {
            if (!result.Ok)
            {
                _log.Warning($"[WorldPortal] heartbeat failed: status={result.StatusCode} {result.Error}");
                return;
            }
            _log.Info($"[WorldPortal] heartbeat ok — instance={result.InstanceId} portals={result.Portals.Count}");
        }

        /// <summary>Tears down the portal loop + client. Safe to call when portals were never started.</summary>
        private void DisposePortal()
        {
            try { _portalLoop?.Dispose(); } catch { /* best-effort */ }
            try { _portalHttp?.Dispose(); } catch { /* best-effort */ }
            try { _installKey?.Dispose(); } catch { /* best-effort */ }
            _portalLoop = null; _portal = null; _portalHttp = null; _installKey = null;
        }
    }
}
