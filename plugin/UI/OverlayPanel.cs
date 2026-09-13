using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WorldScreen.UI
{
    /// <summary>
    /// The plugin's control overlay: a small window to paste a video URL / file path and Load it onto the
    /// world screen, plus a Stop/Pattern button and a live status line. Built from the framework's
    /// declarative window elements. All construction + status updates run on the Unity main thread.
    /// </summary>
    internal sealed class OverlayPanel
    {
        private readonly Action<string> _load;      // the plugin's LoadSource(spec)
        private readonly Action<string> _onCmd;     // the plugin's HandleControl(cmd)
        private readonly string[] _qualityLabels;   // e.g. ["360p","480p","720p"]
        private readonly Func<int> _currentQuality; // plugin's current quality index
        private readonly Action<int> _onQuality;    // the plugin's SetQuality(index)
        private readonly Action? _onPlacePortal;    // SP-1c: place a shared portal here (null ⇒ no portal UI)
        private readonly Action? _onRemovePortal;   // SP-1c: remove my shared portal
        private readonly Func<string>? _portalStatus; // SP-1c: portal state line
        private readonly IWindowControl _control;
        private string _input = string.Empty;
        private string _status = "Helper: starting…";

        public OverlayPanel(IPluginServices services, Action<string> loadSource, Action<string> onControl,
            string[] qualityLabels, Func<int> currentQuality, Action<int> onQuality, Func<bool>? shouldRender = null,
            Action? onPlacePortal = null, Action? onRemovePortal = null, Func<string>? portalStatus = null)
        {
            _load = loadSource;
            _onCmd = onControl;
            _qualityLabels = qualityLabels;
            _currentQuality = currentQuality;
            _onQuality = onQuality;
            _onPlacePortal = onPlacePortal;
            _onRemovePortal = onRemovePortal;
            _portalStatus = portalStatus;
            var spec = new WindowSpec(
                "worldscreen.overlay", "World Screen",
                new WindowRect(40f, 120f, 320f, 0f),
                WindowCategory.Tools, WindowPanelStyle.Tracker)
            {
                ShouldRender = shouldRender ?? (() => true), // hidden during full-screen (the cinema view is clean)
                StartVisible = true,
                Draggable = true,
                Closable = true,
            };
            _control = services.Windows.Register(new WindowRegistration(spec, BuildRoot()));
        }

        /// <summary>Updates the status line (call on the main thread).</summary>
        public void SetStatus(string status) => _status = status;

        /// <summary>Removes the window.</summary>
        public void Remove() => _control.Remove();

        private HudElement BuildRoot()
        {
            var baseRows = new HudElement[]
            {
            new TextElement(() => _status),
            new SeparatorElement(),
            new TextElement(() => "Paste a video URL or file path:"),
            new RowElement(new HudElement[]
            {
                new InputElement(() => _input, OnSubmit, 220f, s => _input = s),
                new ButtonElement(() => "Load", DoLoad),
            }, 6f),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Stop / Pattern", () => _load("testpattern")),
            }, 6f),
            new SeparatorElement(),
            new TextElement(() => "Move / size the screen:"),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Up", () => _onCmd("up")),
                new ButtonElement(() => "Down", () => _onCmd("down")),
                new ButtonElement(() => "Nearer", () => _onCmd("nearer")),
                new ButtonElement(() => "Farther", () => _onCmd("farther")),
            }, 6f),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Bigger", () => _onCmd("bigger")),
                new ButtonElement(() => "Smaller", () => _onCmd("smaller")),
                new ButtonElement(() => "In front of me", () => _onCmd("replace")),
            }, 6f),
            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new TextElement(() => "Quality:"),
                new DropdownElement(_currentQuality, () => _qualityLabels, _onQuality, 90f),
            }, 6f),
            };
            var portal = PortalSection();
            var all = new HudElement[baseRows.Length + portal.Length];
            Array.Copy(baseRows, all, baseRows.Length);
            Array.Copy(portal, 0, all, baseRows.Length, portal.Length);
            return new ColumnElement(all, 6f) { Padding = 8 };
        }

        // SP-1c: a shared-portal section, only when the plugin wired the callbacks (backend configured).
        private HudElement[] PortalSection()
        {
            if (_onPlacePortal == null) return Array.Empty<HudElement>();
            return new HudElement[]
            {
                new SeparatorElement(),
                new TextElement(() => "Shared portal:"),
                new TextElement(() => _portalStatus?.Invoke() ?? ""),
                new RowElement(new HudElement[]
                {
                    new ButtonElement(() => "Place portal here", () => _onPlacePortal?.Invoke()),
                    new ButtonElement(() => "Remove", () => _onRemovePortal?.Invoke()),
                }, 6f),
            };
        }

        // InputElement fires this on Enter/blur; the Load button fires DoLoad directly.
        private void OnSubmit(string text)
        {
            _input = text;
            DoLoad();
        }

        private void DoLoad()
        {
            var v = (_input ?? string.Empty).Trim();
            if (v.Length == 0) return;
            var isUrl = v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            _load(isUrl ? "url:" + v : "file:" + v);
        }
    }
}
