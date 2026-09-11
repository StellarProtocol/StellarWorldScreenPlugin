using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Stellar.WorldScreen.Net
{
    /// <summary>
    /// Auto-launches the sibling <c>stellar-castbox.exe</c> that ships next to this plugin DLL, via
    /// <see cref="Process.Start(ProcessStartInfo)"/>. On a player's machine both are native Windows
    /// processes; on the dev box the plugin runs inside the game's Proton/Wine prefix and starts the
    /// helper as another Windows process in the same prefix — so the same code path works in both.
    /// Held-handle single-instance guard is per plugin lifecycle; <see cref="Stop"/> kills the child.
    /// </summary>
    internal sealed class HelperLauncher : IDisposable
    {
        private readonly Action<string>? _log;
        private Process? _proc;

        public HelperLauncher(Action<string>? log = null) => _log = log;

        /// <summary>Full path to <c>stellar-castbox.exe</c> next to the executing plugin assembly.</summary>
        public static string ResolveExePath()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "stellar-castbox.exe");
        }

        /// <summary>Starts the helper with <paramref name="args"/> if it isn't already running for this instance.</summary>
        public void EnsureRunning(string exePath, string args)
        {
            if (_proc != null && !_proc.HasExited) return;
            if (!File.Exists(exePath))
            {
                _log?.Invoke($"[HelperLauncher] helper not found at {exePath} — is stellar-castbox.exe deployed beside the plugin?");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ".",
            };
            try
            {
                _proc = Process.Start(psi);
                _log?.Invoke($"[HelperLauncher] started {Path.GetFileName(exePath)} pid={_proc?.Id} args='{args}'");
            }
            catch (Exception ex)
            {
                _proc = null;
                _log?.Invoke($"[HelperLauncher] failed to start helper: {ex.Message}");
            }
        }

        /// <summary>Stops the current helper (if any) and relaunches it with new args (e.g. a new source).</summary>
        public void Restart(string exePath, string args)
        {
            Stop();
            EnsureRunning(exePath, args);
        }

        /// <summary>Kills the launched helper if it is still alive. Idempotent.</summary>
        public void Stop()
        {
            try
            {
                if (_proc != null && !_proc.HasExited) _proc.Kill();
            }
            catch (Exception) { /* already gone */ }
            try { _proc?.Dispose(); } catch (Exception) { }
            _proc = null;
        }

        public void Dispose() => Stop();
    }
}
