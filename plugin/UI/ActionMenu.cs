using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WorldScreen.UI
{
    /// <summary>
    /// A bottom control bar for the world screen: a horizontal row of playback actions shown by the framework
    /// only while the player is close to the screen OR while full-screen is active. In full-screen it is the
    /// only visible UI (the video backdrop hides everything else). Framework window; main thread only.
    /// </summary>
    internal sealed class ActionMenu
    {
        private readonly IWindowControl _control;

        public ActionMenu(
            IPluginServices services,
            Func<bool> shouldRender,
            Func<bool> isFullscreen,
            Action toggleFullscreen,
            Func<bool> isPlaying,
            Action togglePause,
            Action stop,
            Action<string> onCmd,             // move/size (the plugin's HandleControl)
            string[] qualityLabels,
            Func<int> currentQuality,
            Action<int> onQuality,
            Func<bool> isAutoFace,            // whether the screen auto-turns to face the viewer
            Action toggleAutoFace)
        {
            // Bottom-centre-ish (assumes ~1080p; draggable if off). y is from the top.
            var spec = new WindowSpec(
                "worldscreen.actions", "Screen Controls",
                new WindowRect(700f, 980f, 0f, 0f),
                WindowCategory.Tools, WindowPanelStyle.Tracker)
            {
                // Open from the start; ShouldRender does the proximity/full-screen gating each frame (a window
                // left StartVisible=false stays CLOSED and ShouldRender never reopens it — that hid the menu).
                ShouldRender = shouldRender,
                StartVisible = true,
                Draggable = true,
                Closable = false,
            };
            var root = new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new ButtonElement(() => isPlaying() ? "Pause" : "Play", togglePause),
                    new ButtonElement(() => "Stop", stop),
                    new ButtonElement(() => isFullscreen() ? "Exit full screen" : "Full screen", toggleFullscreen),
                }, 8f),
                new SeparatorElement(),
                new TextElement(() => "Move / size the screen:"),
                new RowElement(new HudElement[]
                {
                    new ButtonElement(() => "Up", () => onCmd("up")),
                    new ButtonElement(() => "Down", () => onCmd("down")),
                    new ButtonElement(() => "Nearer", () => onCmd("nearer")),
                    new ButtonElement(() => "Farther", () => onCmd("farther")),
                }, 6f),
                new RowElement(new HudElement[]
                {
                    new ButtonElement(() => "Bigger", () => onCmd("bigger")),
                    new ButtonElement(() => "Smaller", () => onCmd("smaller")),
                    new ButtonElement(() => isAutoFace() ? "Facing: auto" : "Facing: fixed", toggleAutoFace),
                }, 6f),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => "Quality:"),
                    new DropdownElement(currentQuality, () => qualityLabels, onQuality, 90f),
                }, 6f),
            }, 6f)
            { Padding = 8 };
            _control = services.Windows.Register(new WindowRegistration(spec, root));
        }

        /// <summary>Removes the window.</summary>
        public void Remove() => _control.Remove();
    }
}
