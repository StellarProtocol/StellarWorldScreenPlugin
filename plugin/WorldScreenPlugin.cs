using System;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.WorldScreen;

/// <summary>
/// World Screen plugin entry point. Streams video frames from the `stellar-castbox` Rust helper (over
/// localhost TCP — see docs/protocol.md and Net/WireCodec.cs) into an in-world uGUI screen. Scaffold
/// only: no wiring yet — the framework instantiates this via its single <see cref="IPluginServices"/>
/// constructor and later tasks add the helper connection, window, and rendering.
/// </summary>
public sealed class WorldScreenPlugin : IStellarPlugin
{
    private readonly IPluginServices _services;

    public WorldScreenPlugin(IPluginServices services)
    {
        _services = services;
    }

    public string Name => "World Screen";

    public void Dispose()
    {
    }
}
