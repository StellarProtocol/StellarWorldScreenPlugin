using System.Collections.Generic;
using UnityEngine;
using Stellar.WorldScreen.Net;

namespace Stellar.WorldScreen.World
{
    /// <summary>
    /// Renders the set of portals the backend reports in each heartbeat: one <see cref="PortalProp"/> marker
    /// per portal, spawned/repositioned/despawned as the reported list changes. SP-1c first render increment
    /// (markers only). All calls are main-thread (Apply from the posted heartbeat result; Tick from OnUpdate).
    /// </summary>
    internal sealed class PortalWorld
    {
        private readonly Dictionary<string, PortalProp> _props = new();
        private readonly Dictionary<string, PortalInfo> _infos = new(); // latest reported info per portal
        private readonly HashSet<string> _seen = new();

        /// <summary>Reconciles the live prop set to <paramref name="portals"/>: adds new, updates existing,
        /// removes any that vanished from the report.</summary>
        public void Apply(IReadOnlyList<PortalInfo> portals)
        {
            _seen.Clear();
            foreach (var p in portals)
            {
                _seen.Add(p.PortalId);
                _infos[p.PortalId] = p;
                if (!_props.TryGetValue(p.PortalId, out var prop))
                {
                    prop = new PortalProp();
                    _props[p.PortalId] = prop;
                }
                prop.Place(new Vector3((float)p.PosX, (float)p.PosY, (float)p.PosZ), LabelFor(p));
            }

            // Despawn any prop no longer in the report.
            if (_props.Count != _seen.Count)
            {
                var stale = new List<string>();
                foreach (var id in _props.Keys) if (!_seen.Contains(id)) stale.Add(id);
                foreach (var id in stale) { _props[id].Destroy(); _props.Remove(id); _infos.Remove(id); }
            }
        }

        /// <summary>The id of the portal nearest <paramref name="playerPos"/> on the ground plane (null when no
        /// portals), with its ground distance in <paramref name="dist"/>.</summary>
        public string? FindNearest(Vector3 playerPos, out float dist)
        {
            string? best = null;
            dist = float.MaxValue;
            foreach (var kv in _infos)
            {
                var p = kv.Value;
                float dx = (float)p.PosX - playerPos.x, dz = (float)p.PosZ - playerPos.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < dist) { dist = d; best = kv.Key; }
            }
            return best;
        }

        /// <summary>Latest reported info for a portal (false if it is not currently present).</summary>
        public bool TryGetInfo(string portalId, out PortalInfo info) => _infos.TryGetValue(portalId, out info!);

        /// <summary>Shows the "walk up to watch" prompt on exactly one portal (<paramref name="portalId"/>),
        /// hiding it on all others. Pass null to hide every prompt.</summary>
        public void SetPromptOn(string? portalId)
        {
            foreach (var kv in _props) kv.Value.SetPrompt(kv.Key == portalId);
        }

        /// <summary>Per-frame: billboard + animate every beacon.</summary>
        public void Tick(Camera? cam)
        {
            float time = Time.time;
            foreach (var prop in _props.Values) prop.Tick(cam, time);
        }

        public void Destroy()
        {
            foreach (var prop in _props.Values) prop.Destroy();
            _props.Clear();
            _infos.Clear();
            _seen.Clear();
        }

        private static string LabelFor(PortalInfo p)
        {
            var owner = string.IsNullOrEmpty(p.OwnerName) ? "Portal" : p.OwnerName;
            var watchers = p.WatcherCount > 0 ? $"\n▶ {p.WatcherCount} watching" : "";
            return $"★ {owner}{watchers}";
        }
    }
}
