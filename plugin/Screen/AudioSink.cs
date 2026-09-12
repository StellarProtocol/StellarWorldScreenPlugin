using System;

namespace Stellar.WorldScreen.Screen
{
    /// <summary>
    /// Thread-safe PCM ring buffer bridging the socket thread (<see cref="Submit"/>, interleaved S16LE
    /// bytes from AUDIO messages) to Unity's audio thread (<see cref="ReadInto"/>, interleaved float
    /// samples pulled by a streaming AudioClip's PCM reader callback). Fixed format: 48000 Hz, 2 channels.
    /// On underrun <see cref="ReadInto"/> fills silence; on overrun <see cref="Submit"/> drops the newest
    /// samples so latency stays bounded. Pure BCL — no UnityEngine, so it can be unit-tested off-game.
    /// </summary>
    public sealed class AudioSink
    {
        private readonly object _lock = new();
        private readonly float[] _ring;
        private int _read;
        private int _count;

        /// <summary>Total samples ever accepted (dropped-on-overrun excluded). Diagnostic: proves wire flow.</summary>
        public long SubmittedSamples { get; private set; }

        /// <param name="capacitySamples">Ring size in samples (interleaved). Default ≈ 1s of 48k stereo.</param>
        public AudioSink(int capacitySamples = 48000 * 2)
        {
            _ring = new float[Math.Max(1024, capacitySamples)];
        }

        /// <summary>Enqueues interleaved little-endian S16 PCM (called from the socket thread).</summary>
        public void Submit(ReadOnlySpan<byte> s16le)
        {
            int samples = s16le.Length / 2;
            lock (_lock)
            {
                for (int i = 0; i < samples; i++)
                {
                    if (_count >= _ring.Length) break; // overrun — drop the rest, keep latency bounded
                    short s = (short)(s16le[i * 2] | (s16le[i * 2 + 1] << 8));
                    _ring[(_read + _count) % _ring.Length] = s / 32768f;
                    _count++;
                    SubmittedSamples++;
                }
            }
        }

        /// <summary>Fills <paramref name="data"/> with the next samples, silence on underrun (audio thread).</summary>
        public void ReadInto(float[] data)
        {
            lock (_lock)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (_count > 0)
                    {
                        data[i] = _ring[_read];
                        _read = (_read + 1) % _ring.Length;
                        _count--;
                    }
                    else
                    {
                        data[i] = 0f;
                    }
                }
            }
        }

        /// <summary>Drops all buffered audio (on a source change/stop) so the new source starts clean.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _read = 0;
                _count = 0;
            }
        }
    }
}
