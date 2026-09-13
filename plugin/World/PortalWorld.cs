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
        private readonly HashSet<string> _seen = new();

        /// <summary>Reconciles the live prop set to <paramref name="portals"/>: adds new, updates existing,
        /// removes any that vanished from the report.</summary>
        public void Apply(IReadOnlyList<PortalInfo> portals)
        {
            _seen.Clear();
            foreach (var p in portals)
            {
                _seen.Add(p.PortalId);
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
                foreach (var id in stale) { _props[id].Destroy(); _props.Remove(id); }
            }
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
