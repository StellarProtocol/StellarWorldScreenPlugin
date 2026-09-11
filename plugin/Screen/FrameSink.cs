// Latest-wins single-slot frame buffer for the plugin<->helper video path.
//
// PURE BCL — System.* only. No UnityEngine — this file is compiled directly into the off-game unit-test
// project (see tests/Stellar.WorldScreen.Tests.csproj), same as Net/WireCodec.cs.
using System;

namespace Stellar.WorldScreen.Screen;

/// <summary>
/// Latest-wins single-slot frame buffer. <see cref="Submit"/> is called from the background socket
/// thread (once per decoded FRAME); <see cref="TryTakeLatest"/> is called from the main thread (once per
/// tick) to pull whatever frame is newest for texture upload. This is a mailbox, not a queue — a frame
/// that arrives while the previous one is still untaken is simply overwritten, and only the most recent
/// Submit is ever observed by TryTakeLatest.
/// </summary>
public sealed class FrameSink
{
    private readonly object _lock = new();
    private byte[] _buffer = Array.Empty<byte>();
    private int _w;
    private int _h;
    private byte _pixfmt;
    private bool _dirty;

    /// <summary>
    /// Copies <paramref name="pixels"/> into the sink's reused internal buffer — reallocated only when
    /// the byte length changes (i.e. w*h*bpp changed) — and marks a new frame available. Called from the
    /// background socket thread.
    /// </summary>
    public void Submit(int w, int h, byte pixfmt, ReadOnlySpan<byte> pixels)
    {
        lock (_lock)
        {
            if (_buffer.Length != pixels.Length)
                _buffer = new byte[pixels.Length];
            pixels.CopyTo(_buffer);
            _w = w;
            _h = h;
            _pixfmt = pixfmt;
            _dirty = true;
        }
    }

    /// <summary>
    /// If a new frame arrived since the last take, returns <c>true</c> with the latest width/height and
    /// the sink's buffer, and clears the dirty flag. Returns <c>false</c> — with <paramref name="w"/>/
    /// <paramref name="h"/> zeroed and <paramref name="buffer"/> empty — if nothing new arrived since the
    /// last take (or nothing has ever been submitted). Called from the main thread only.
    /// </summary>
    /// <remarks>
    /// <paramref name="buffer"/> is the sink's REUSED buffer, not a copy: the caller must upload it (e.g.
    /// to a GPU texture) immediately and must not retain a reference to it past the next
    /// <see cref="Submit"/> call, which may overwrite its contents or replace it outright.
    /// </remarks>
    public bool TryTakeLatest(out int w, out int h, out byte[] buffer)
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                w = 0;
                h = 0;
                buffer = Array.Empty<byte>();
                return false;
            }

            w = _w;
            h = _h;
            buffer = _buffer;
            _dirty = false;
            return true;
        }
    }
}
