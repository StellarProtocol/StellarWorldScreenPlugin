using System;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;
using Stellar.Abstractions.Domain;
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

        private string _portalStatus = "";     // overlay line (main thread only)
        private string? _myPortalId;           // the portal this client placed (for Remove)
        private readonly PortalWorld _portalWorld = new(); // renders a marker per reported portal

        // Walk-up activation (SP-1c): the video screen appears above a placed beacon ONLY when the player
        // activates it (press F within range); no auto-placed personal screen.
        private const float ActivateRangeM = 4f;    // show the prompt + accept the key within this ground distance
        private const float DeactivateRangeM = 9f;   // auto-hide the screen once the player walks past this
        private string? _activePortalId;             // the portal whose screen is up (null = none active)
        private bool _screenPlaced;                  // placed the screen at the beacon for THIS activation yet?
        private bool _screenAutoFace = true;         // continuously turn the screen to face the viewer (toggle in Screen Controls)
        private bool _hasSavedScreen;                // a saved screen placement exists (remembered across activations)
        private Vector3 _savedScreenOffset;          // saved screen position as an offset from the beacon
        private Quaternion _savedScreenRot = Quaternion.identity;
        private float _savedScreenWidth;
        private Vector3 _lastBeacon;                 // beacon pos at the last placement (for the offset math)
        private IConfigSection? _screenPrefs;        // persists the screen setup in plugindata (across restarts)
        private IHotkeyAction? _activateAction;      // framework hotkey (default F, user-rebindable) → activate/deactivate
        private bool _activateRequested;             // set by the hotkey callback, consumed next frame by the state machine

        /// <summary>Overlay status line for the shared-portal section.</summary>
        internal string PortalStatus() => _portal == null ? "" : _portalStatus;

        /// <summary>Constructs the portal client + starts its heartbeat loop when a backend URL is configured.
        /// Fully guarded: any failure here logs and leaves the standalone video screen fully working.</summary>
        private void InitPortal()
        {
            try
            {
                // Activation key via the framework hotkey service (the proven, user-rebindable input path every
                // other plugin uses) — NOT raw Input polling, which missed presses. Shows in Settings → Hotkeys.
                _activateAction = _services.Hotkeys.DeclareAction(
                    new HotkeyAction("worldportal.activate", "Activate nearby portal (watch)", new KeyBinding(StellarKeyCode.F)),
                    callback: () => _activateRequested = true);
                DeclareDjHotkeys(); // SP-2b: next/prev for when the player DJs their own portal
                LoadScreenPrefs();  // SP-2b: restore the persisted screen setup (placement/quality/auto-face)

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
            _portalWorld.Tick(GetActiveCamera()); // per-frame billboard of every portal marker
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
            CaptureServerClock(result);          // SP-2b: refresh the local→server clock offset for viewer sync
            ReclaimOwnPortal(result);            // SP-2b: re-adopt our own portal after a restart (DJ/Remove)
            _portalWorld.Apply(result.Portals);  // render a marker for each reported portal
        }

        // After a restart the client forgets it placed a portal (_myPortalId is null), so activating it makes the
        // owner a mere VIEWER (dj=False) and Remove refuses. The heartbeat reports each portal's ownerCharId, so
        // re-adopt the one owned by THIS character — restoring DJ + Remove rights without having to re-place.
        private void ReclaimOwnPortal(HeartbeatResult result)
        {
            if (_myPortalId != null) return;
            long myCharId = _services.PlayerState.CharId;
            if (myCharId == 0) return;
            foreach (var p in result.Portals)
            {
                if (p.OwnerCharId == myCharId)
                {
                    _myPortalId = p.PortalId;
                    // Seed the draft from the reclaimed portal so the DJ + editor reflect its current playlist.
                    if (_draftPlaylist.Count == 0)
                        foreach (var it in PortalPlaylist.Parse(p.SourceKind, p.Source)) _draftPlaylist.Add(it);
                    _log.Info($"[WorldPortal] re-claimed own portal {p.PortalId} ({_draftPlaylist.Count} item(s))");
                    break;
                }
            }
        }

        /// <summary>Main-thread per-frame (from <c>UpdateAvPro</c>): walk-up activation. Prompts the nearest
        /// beacon in range; F raises the screen on it (loading its source); walking away or pressing F again
        /// hides it. Only one portal is active at a time; activation is local to this viewer.</summary>
        private void UpdatePortalActivation(float dt)
        {
            var pp = _services.PlayerState.Position;
            var player = new Vector3(pp.X, pp.Y, pp.Z);

            // The activation key is delivered by the framework hotkey service (reliable + user-rebindable); its
            // callback set _activateRequested — consume it here, on the state-machine frame.
            bool pressed = _activateRequested;
            _activateRequested = false;

            var nearest = _portalWorld.FindNearest(player, out float nearDist);
            if (pressed)
                _log.Info($"[WorldPortal] activate key — active={_activePortalId ?? "none"} nearest={nearest ?? "none"} dist={nearDist:F1}m range={ActivateRangeM}m");

            if (_activePortalId != null)
            {
                if (_portalWorld.TryGetInfo(_activePortalId, out var act))
                {
                    var beacon = new Vector3((float)act.PosX, (float)act.PosY, (float)act.PosZ);
                    _screen.SetVisible(true);
                    _playerNear = true;                       // gates the proximity action menu
                    // Place the screen ONCE (when it exists): restore the remembered placement if we have one,
                    // else default above the beacon. Then leave it — so Move/size stick; only re-face each frame
                    // when the viewer has opted into auto-facing.
                    if (!_screenPlaced && _screen.Exists)
                    {
                        if (_hasSavedScreen) _screen.RestorePlacement(beacon + _savedScreenOffset, _savedScreenRot, _savedScreenWidth);
                        else _screen.PlaceAtBeacon(beacon, player);
                        _lastBeacon = beacon;
                        _screenPlaced = true;
                    }
                    else if (_screenAutoFace) _screen.FaceViewer(player);
                    if (GroundDist(beacon, player) > DeactivateRangeM || pressed) Deactivate();
                    else UpdatePlaybackSync(act, dt);         // SP-2b: DJ drives / viewer follows synced playback
                }
                else Deactivate();                            // the active portal vanished (lease expired / removed)
                _portalWorld.SetPromptOn(null);
                return;
            }

            // No active portal → screen hidden. Prompt the nearest beacon in range; the key activates it.
            _screen.SetVisible(false);
            _playerNear = false;
            _nextRequested = _prevRequested = false;          // drop any DJ next/prev presses made while not DJing
            if (nearest != null && nearDist <= ActivateRangeM)
            {
                _portalWorld.SetPromptOn(nearest);
                if (pressed) Activate(nearest);
            }
            else
            {
                _portalWorld.SetPromptOn(null);
                if (_lastVolume != 0) { _avpro.SetVolume(0f); _lastVolume = 0; } // mute the preloaded clip while idle
            }
        }

        // Raises the screen on a portal: sets up its playlist + DJ/viewer role (SP-2b) and loads the current
        // item. AVPro is kept playing even while hidden, so a decoded frame is always ready and the screen shows
        // the instant you activate.
        private void Activate(string portalId)
        {
            if (!_portalWorld.TryGetInfo(portalId, out var info)) return;
            _activePortalId = portalId;
            _screenPlaced = false; // place the screen at the beacon on this activation
            OnActivatePlayback(info);
            _lastVolume = -1; // force distance volume to re-apply for the newly shown screen
            _log.Info($"[WorldPortal] activated {portalId} (dj={_myPortalId == portalId}, items={_activePlaylist.Count}, kind={info.SourceKind ?? "none"})");
        }

        // Hides the screen and mutes audio (playback keeps running muted so re-activation is instant).
        private void Deactivate()
        {
            // Remember the viewer's screen placement (offset from the beacon + rotation + size) so re-activating
            // restores it instead of snapping back to the default.
            if (_screenPlaced && _screen.Exists)
            {
                _savedScreenOffset = _screen.Position - _lastBeacon;
                _savedScreenRot = _screen.Rotation;
                _savedScreenWidth = _screen.WidthMetres;
                _hasSavedScreen = true;
                SaveScreenPrefs(); // persist the placement so it survives a client restart
            }
            _activePortalId = null;
            _screenPlaced = false;
            _screen.SetVisible(false);
            _playerNear = false;
            if (_fullscreen.Visible) _fullscreen.Hide();
            _avpro.SetVolume(0f); _lastVolume = 0;
            _log.Info("[WorldPortal] deactivated");
        }

        private static float GroundDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Screen Controls: continuous auto-facing (the screen turns to face you) — on/off + state.</summary>
        internal void ToggleScreenAutoFace() { _screenAutoFace = !_screenAutoFace; SaveScreenPrefs(); }
        internal bool ScreenAutoFace => _screenAutoFace;

        // Loads the persisted screen setup from plugindata (called once at startup). All guarded — a fresh
        // install / missing keys fall back to defaults.
        private void LoadScreenPrefs()
        {
            try
            {
                _screenPrefs = _services.Config.GetSection("worldscreen");
                _screenAutoFace = _screenPrefs.Get<bool>("autoFace", true);
                _quality = _screenPrefs.Get<int>("quality", _quality);
                _hasSavedScreen = _screenPrefs.Get<bool>("hasPlacement", false);
                if (_hasSavedScreen)
                {
                    _savedScreenOffset = new Vector3(
                        _screenPrefs.Get<float>("offX", 0f),
                        _screenPrefs.Get<float>("offY", 2.4f),
                        _screenPrefs.Get<float>("offZ", 0f));
                    _savedScreenRot = Quaternion.Euler(0f, _screenPrefs.Get<float>("yaw", 0f), 0f);
                    _savedScreenWidth = _screenPrefs.Get<float>("width", 3f);
                }
            }
            catch (Exception) { /* config unavailable → in-memory only */ }
        }

        // Persists the current screen setup to plugindata. Called on placement/auto-face/quality changes.
        private void SaveScreenPrefs()
        {
            if (_screenPrefs == null) return;
            try
            {
                _screenPrefs.Set("autoFace", _screenAutoFace);
                _screenPrefs.Set("quality", _quality);
                _screenPrefs.Set("hasPlacement", _hasSavedScreen);
                _screenPrefs.Set("offX", _savedScreenOffset.x);
                _screenPrefs.Set("offY", _savedScreenOffset.y);
                _screenPrefs.Set("offZ", _savedScreenOffset.z);
                _screenPrefs.Set("yaw", _savedScreenRot.eulerAngles.y);
                _screenPrefs.Set("width", _savedScreenWidth);
                _screenPrefs.Save();
            }
            catch (Exception) { /* best-effort */ }
        }

        /// <summary>Overlay "Place portal here" (MAIN THREAD — button click): place a shared portal at the
        /// player's current position + instance with the current video source. On success the portal exists in
        /// the backend registry and shows up in every co-located client's heartbeat.</summary>
        internal void PlacePortalHere()
        {
            if (_portal == null) { _portalStatus = "backend off"; return; }
            var hb = InstanceProbe.Gather(_services); // main thread (button click) — reads live game state
            if (hb == null) { _portalStatus = "not in world"; return; }

            // SP-2b: a built-up draft playlist places as sourceKind="playlist" (JSON in sourceUrl); otherwise
            // the single current source (SP-1 behaviour).
            string? kind, url;
            if (_draftPlaylist.Count > 0) { kind = "playlist"; url = PortalPlaylist.Serialize(_draftPlaylist); }
            else { (kind, url) = ParsePortalSource(_currentSource); }

            var cam = GetActiveCamera();
            double yaw = cam != null ? PortalClient.RoundSignedCanonicalFloat(cam.transform.eulerAngles.y) : 0.0;
            var ownerName = _services.PlayerState.Name ?? "Player";

            var body = new PlaceBody(
                Region: hb.Region, MapId: hb.MapId, SceneId: hb.SceneId, LineId: hb.LineId,
                PosX: hb.PosX, PosY: hb.PosY, PosZ: hb.PosZ, Yaw: yaw,
                OwnerCharId: hb.CharId, SourceKind: kind, SourceUrl: url, Nonce: null);

            _portalStatus = "placing…";
            _portal.PlaceAsync(body, ownerName).ContinueWith(t =>
            {
                var r = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                _services.Framework.Post(() =>
                {
                    if (r != null && r.Ok)
                    {
                        _myPortalId = r.PortalId;
                        _portalStatus = "placed ✓";
                        _log.Info($"[WorldPortal] placed portal {r.PortalId} @ ({hb.PosX},{hb.PosY},{hb.PosZ})");
                    }
                    else
                    {
                        _portalStatus = $"place failed: {r?.StatusCode.ToString() ?? "?"} {r?.Error ?? t.Exception?.Message ?? ""}";
                        _log.Warning("[WorldPortal] " + _portalStatus);
                    }
                });
            });
        }

        /// <summary>Overlay "Remove" (MAIN THREAD): remove the portal this client placed.</summary>
        internal void RemoveMyPortal()
        {
            if (_portal == null || _myPortalId == null) { _portalStatus = "no portal to remove"; return; }
            var id = _myPortalId;
            var charId = _services.PlayerState.CharId;
            _portalStatus = "removing…";
            _portal.RemoveAsync(id, new DeleteBody(id, charId, null)).ContinueWith(t =>
            {
                var r = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                _services.Framework.Post(() =>
                {
                    if (r != null && r.Ok) { _myPortalId = null; _portalStatus = "removed ✓"; }
                    else { _portalStatus = $"remove failed: {r?.StatusCode.ToString() ?? "?"}"; }
                });
            });
        }

        // Maps the plugin's current video source spec to the portal's (kind, url). testpattern/unknown ⇒ no
        // video (an empty prop) — a portal carries a URL/file source, not the local test pattern.
        private static (string? kind, string? url) ParsePortalSource(string src)
        {
            if (src.StartsWith("url:", StringComparison.Ordinal)) return ("url", src.Substring(4));
            if (src.StartsWith("file:", StringComparison.Ordinal)) return ("file", src.Substring(5));
            return (null, null);
        }

        /// <summary>Tears down the portal loop + client. Safe to call when portals were never started.</summary>
        private void DisposePortal()
        {
            try { _activateAction?.Dispose(); } catch { /* best-effort */ }
            try { DisposeDjHotkeys(); } catch { /* best-effort */ }
            try { _portalLoop?.Dispose(); } catch { /* best-effort */ }
            try { _portalWorld.Destroy(); } catch { /* best-effort */ }
            try { _portalHttp?.Dispose(); } catch { /* best-effort */ }
            try { _installKey?.Dispose(); } catch { /* best-effort */ }
            _activateAction = null;
            _portalLoop = null; _portal = null; _portalHttp = null; _installKey = null;
        }
    }
}
