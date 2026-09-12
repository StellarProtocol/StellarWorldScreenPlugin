using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WorldScreen.UI
{
    /// <summary>
    /// A compact "you're near the screen" action menu, shown by the framework only while the player is close
    /// to the world screen (or while full-screen is active, so it can be exited). Offers full-screen toggle,
    /// play/pause, and stop. Built from the framework's declarative window elements; main thread only.
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
            Action stop)
        {
            var spec = new WindowSpec(
                "worldscreen.actions", "Screen Actions",
                new WindowRect(700f, 200f, 220f, 0f),
                WindowCategory.Tools, WindowPanelStyle.Tracker)
            {
                ShouldRender = shouldRender,   // framework hides the window when the player walks away
                StartVisible = false,
                Draggable = true,
                Closable = false,
            };
            var root = new ColumnElement(new HudElement[]
            {
                new TextElement(() => "Screen nearby"),
                new SeparatorElement(),
                new ButtonElement(() => isFullscreen() ? "Exit full screen" : "Full screen", toggleFullscreen),
                new ButtonElement(() => isPlaying() ? "Pause" : "Play", togglePause),
                new ButtonElement(() => "Stop", stop),
            }, 6f)
            { Padding = 8 };
            _control = services.Windows.Register(new WindowRegistration(spec, root));
        }

        /// <summary>Removes the window.</summary>
        public void Remove() => _control.Remove();
    }
}
