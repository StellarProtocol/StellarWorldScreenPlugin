using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Stellar.Abstractions.Domain;
using Stellar.WorldScreen.Net;
using Stellar.WorldScreen.World;

namespace Stellar.WorldScreen
{
    /// <summary>
    /// SP-2b playlist mode — owner-DJ synced playback. When a portal is active, the client either DRIVES
    /// playback (it owns the portal → the DJ: plays the playlist locally and reports its state to the
    /// backend on each change/advance + periodically) or FOLLOWS it (someone else's portal → the viewer:
    /// loads the DJ's current video and seeks to the shared position, extrapolated from the heartbeat's
    /// <c>serverNowMs</c> clock). No video relay — every client fetches/decodes the video itself; only the
    /// small playback STATE crosses the wire. See
    /// <c>docs/superpowers/specs/2026-09-14-world-portal-sp2-playlist-design.md</c>.
    /// </summary>
    public sealed partial class WorldScreenPlugin
    {
        private const float DjReportIntervalS = 10f;      // periodic DJ report, so late-joiners stay fresh
        private const long SyncDriftToleranceMs = 1500;   // viewer re-seeks only when off by more than this

        private long _serverClockOffsetMs;                // serverNowMs − localNowMs, from the last heartbeat
        private bool _isDj;                               // the active portal is ours → we drive playback
        private IReadOnlyList<PlaylistItem> _activePlaylist = Array.Empty<PlaylistItem>();
        private string? _activePlaylistSource;            // last-parsed source JSON, to re-parse only on change
        private int _djIndex;                             // DJ's current playlist index
        private float _djReportTimer;
        private int _lastReportedIndex = -1;
        private bool _lastReportedPlaying;
        private bool _djReportingDisabled;                // set on a 404 (backend predates SP-2) → play locally only
        private IHotkeyAction? _nextAction, _prevAction;
        private bool _nextRequested, _prevRequested;      // set by the DJ hotkey callbacks

        // The playlist the owner is building in the overlay before placing. Empty ⇒ Place uses the single
        // current source (SP-1 behaviour).
        private readonly List<PlaylistItem> _draftPlaylist = new();

        private static long PortalNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Overlay "Add to playlist" (main thread): append the input-box URL/path (a "url:"/"file:"
        /// spec) to the draft — no need to Load it first.</summary>
        internal void AddToPlaylist(string spec)
        {
            var (kind, url) = ParsePortalSource(spec);
            if (kind == null || string.IsNullOrEmpty(url)) { _portalStatus = "enter a URL/path to add"; return; }
            _draftPlaylist.Add(new PlaylistItem(url!, kind!, null));
            _portalStatus = $"playlist: {_draftPlaylist.Count} video(s)";
            if (_myPortalId != null) PlacePortalHere(); // already have a portal → update it live with the new playlist
        }

        /// <summary>Overlay "Clear playlist" (main thread).</summary>
        internal void ClearDraftPlaylist()
        {
            _draftPlaylist.Clear();
            _portalStatus = "playlist cleared";
            if (_myPortalId != null) PlacePortalHere(); // update the live portal to the (now empty) playlist
        }

        /// <summary>Overlay status: how many videos are in the draft playlist.</summary>
        internal string PlaylistStatus() =>
            _draftPlaylist.Count == 0 ? "playlist: empty (Place uses current video)" : $"playlist: {_draftPlaylist.Count} video(s)";

        /// <summary>Declares the DJ next/prev hotkeys (framework input path — reliable + rebindable). Called
        /// from <see cref="InitPortal"/>. They only act while the player is DJing their own active portal.</summary>
        private void DeclareDjHotkeys()
        {
            // Defaults F9 (next) / F7 (prev): function keys the game doesn't bind (PageDown/PageUp collided).
            // Rebindable in Settings → Hotkeys, and there are on-screen Next/Prev buttons too.
            _nextAction = _services.Hotkeys.DeclareAction(
                new HotkeyAction("worldportal.next", "Portal DJ: next video", new KeyBinding(StellarKeyCode.F9)),
                callback: () => _nextRequested = true);
            _prevAction = _services.Hotkeys.DeclareAction(
                new HotkeyAction("worldportal.prev", "Portal DJ: previous video", new KeyBinding(StellarKeyCode.F7)),
                callback: () => _prevRequested = true);
        }

        private void DisposeDjHotkeys()
        {
            try { _nextAction?.Dispose(); } catch { /* best-effort */ }
            try { _prevAction?.Dispose(); } catch { /* best-effort */ }
            _nextAction = null; _prevAction = null;
        }

        /// <summary>Main-thread (from <see cref="OnPortalHeartbeat"/>): update the local→server clock offset from
        /// the heartbeat's <c>serverNowMs</c>, so the viewer can extrapolate the DJ's live position.</summary>
        private void CaptureServerClock(HeartbeatResult result)
        {
            if (result.ServerNowMs > 0) _serverClockOffsetMs = result.ServerNowMs - PortalNowMs();
        }

        /// <summary>Called from <see cref="Activate"/>: set up playlist + DJ/viewer roles for the newly active
        /// portal. The DJ starts playing its current item; the viewer's per-frame sync loads + seeks instead.</summary>
        private void OnActivatePlayback(PortalInfo info)
        {
            _activePlaylist = PortalPlaylist.Parse(info.SourceKind, info.Source);
            _activePlaylistSource = info.Source;
            _isDj = _myPortalId != null && _activePortalId == _myPortalId;
            _avpro.SetLoop(_activePlaylist.Count <= 1); // a single item loops; a real playlist advances instead
            if (_isDj)
            {
                _djIndex = ClampIndex(info.Playback.Index);
                _lastReportedIndex = -1; _lastReportedPlaying = false;
                _djReportTimer = DjReportIntervalS; // force an immediate report on the first DJ tick
                LoadPlaylistItem(_djIndex);
            }
            // Viewer: ViewerTick loads/seeks per frame from the live playback state — nothing to load here.
        }

        /// <summary>Per-frame while a portal is active (from <see cref="UpdatePortalActivation"/>): drive (DJ) or
        /// follow (viewer) playback.</summary>
        private void UpdatePlaybackSync(PortalInfo info, float dt)
        {
            // Keep the active playlist in sync with live edits (the owner adding/clearing re-places the portal).
            if (info.Source != _activePlaylistSource)
            {
                _activePlaylist = PortalPlaylist.Parse(info.SourceKind, info.Source);
                _activePlaylistSource = info.Source;
            }
            bool next = _nextRequested, prev = _prevRequested;
            _nextRequested = _prevRequested = false;
            if (_isDj) DjTick(dt, next, prev);
            else ViewerTick(info);
        }

        // ---- DJ ----

        private void DjTick(float dt, bool next, bool prev)
        {
            if (next) DjAdvance(+1);
            else if (prev) DjAdvance(-1);
            else if (_activePlaylist.Count > 1)
            {
                // Auto-advance when the current (non-looping) item reaches its end.
                double dur = _avpro.Duration, pos = _avpro.CurrentTime;
                if (dur > 0.5 && pos >= dur - 0.5) DjAdvance(+1);
            }

            _djReportTimer += dt;
            bool changed = _djIndex != _lastReportedIndex || _avpro.IsPlaying != _lastReportedPlaying;
            if (changed || _djReportTimer >= DjReportIntervalS) DjReport();
        }

        private void DjAdvance(int delta)
        {
            int count = _activePlaylist.Count;
            if (count == 0) return;
            _djIndex = ((_djIndex + delta) % count + count) % count; // wrap both directions
            LoadPlaylistItem(_djIndex);
            DjReport(); // index changed → report immediately
        }

        // Reports the DJ's current playback state to the backend (fire-and-forget). A 404 means the backend
        // predates SP-2 → disable reporting and just keep playing locally (SP-1 degrade).
        private void DjReport()
        {
            _lastReportedIndex = _djIndex;
            _lastReportedPlaying = _avpro.IsPlaying;
            _djReportTimer = 0f;
            if (_portal == null || _myPortalId == null || _djReportingDisabled) return;

            long posMs = (long)(_avpro.CurrentTime * 1000.0);
            var body = new PlaybackBody(_myPortalId, _djIndex, posMs, _avpro.IsPlaying, null);
            _portal.SetPlaybackAsync(_myPortalId, body).ContinueWith(t =>
            {
                var r = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (r != null && r.StatusCode == 404)
                    _services.Framework.Post(() =>
                    {
                        _djReportingDisabled = true;
                        _log.Warning("[WorldPortal] playback PATCH 404 — backend predates SP-2; DJ reporting off (local play only)");
                    });
            });
        }

        // ---- viewer ----

        private void ViewerTick(PortalInfo info)
        {
            var pb = info.Playback;
            int idx = ClampIndex(pb.Index);
            string spec = _activePlaylist.Count > 0 ? SpecForItem(_activePlaylist[idx]) : "testpattern";
            if (spec != _currentSource) { LoadSource(spec); return; } // loading the DJ's current video; seek once it's up

            long serverNow = PortalNowMs() + _serverClockOffsetMs;
            long livePosMs = pb.PositionMs + (pb.Playing ? serverNow - pb.UpdatedMs : 0);
            long durMs = (long)(_avpro.Duration * 1000.0);
            if (livePosMs < 0) livePosMs = 0;
            if (durMs > 0 && livePosMs > durMs) livePosMs = durMs;

            long curMs = (long)(_avpro.CurrentTime * 1000.0);
            if (Math.Abs(curMs - livePosMs) > SyncDriftToleranceMs) _avpro.Seek(livePosMs / 1000.0);

            if (pb.Playing && !_avpro.IsPlaying) _avpro.TogglePause();
            else if (!pb.Playing && _avpro.IsPlaying) _avpro.TogglePause();
        }

        // ---- shared helpers ----

        private int ClampIndex(int idx) =>
            _activePlaylist.Count == 0 ? 0 : Mathf.Clamp(idx, 0, _activePlaylist.Count - 1);

        private void LoadPlaylistItem(int idx)
        {
            LoadSource(_activePlaylist.Count == 0 ? "testpattern" : SpecForItem(_activePlaylist[ClampIndex(idx)]));
        }

        private static string SpecForItem(PlaylistItem item)
        {
            if (string.IsNullOrEmpty(item.Url)) return "testpattern";
            if (item.Kind == "url") return "url:" + item.Url;
            if (item.Kind == "file") return "file:" + item.Url;
            return "testpattern";
        }
    }
}
