// Ported from dr89mh/aion2-cube-radar (GPL-3.0): src/CubeRadar.Cli/CubeSound.cs and
// src/CubeRadar.Core/Audio/WavVolume.cs (beep part). Changes: namespace, LibraryImport, no custom .wav file.
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Services;

/// <summary>
/// The Hidden Cube alarm: Cube Radar's two-tone beep (1200 Hz, pause, 1600 Hz) at an adjustable volume, played
/// through a waveOut device. A new beep replaces one that is still playing. Local audio only.
/// </summary>
internal static partial class HiddenCubeSound
{
    private const int SampleRate = 44_100;
    private const double BeepPeak = 0.7;
    private const uint WaveMapper = 0xFFFFFFFF;
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly object Gate = new();
    private static nint _device, _header, _samples;

    /// <summary>Volume 0–100 % to an amplitude factor; squared so the slider feels even to the ear.</summary>
    internal static double Gain(int volumePercent) => Math.Pow(Math.Clamp(volumePercent, 0, 100) / 100.0, 2);

    /// <summary>Plays the beep; false when no output device could be opened.</summary>
    public static bool Play(int volumePercent)
    {
        if (volumePercent <= 0) return false;
        try { return Start(Beep(volumePercent)); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return false;
        }
    }

    public static void Close()
    {
        lock (Gate) Release();
    }

    /// <summary>16-bit mono PCM WAV of the beep with 5 ms fades against clicks.</summary>
    internal static byte[] Beep(int volumePercent)
    {
        (int Hz, int Ms)[] parts = [(1200, 120), (0, 20), (1600, 160)];
        var amplitude = Gain(volumePercent) * BeepPeak * short.MaxValue;
        var fade = SampleRate * 5 / 1000;
        var samples = new List<short>();
        foreach (var (hz, ms) in parts)
        {
            var n = SampleRate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)fade);
                samples.Add(hz == 0 ? (short)0 : (short)Math.Round(Math.Sin(2 * Math.PI * hz * i / SampleRate) * envelope * amplitude));
            }
        }
        var data = samples.Count * 2;
        var wav = new byte[44 + data];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], data);
        for (var i = 0; i < samples.Count; i++) BinaryPrimitives.WriteInt16LittleEndian(s[(44 + 2 * i)..], samples[i]);
        return wav;
    }

    private static bool Start(byte[] wav)
    {
        const int formatOffset = 20, formatLength = 16, dataOffset = 44;
        var dataLength = wav.Length - dataOffset;
        lock (Gate)
        {
            Release();
            // WAVEFORMATEX: the 16-byte PCM fmt chunk plus a zero cbSize.
            var format = Marshal.AllocHGlobal(18);
            try
            {
                Marshal.Copy(new byte[18], 0, format, 18);
                Marshal.Copy(wav, formatOffset, format, formatLength);
                if (waveOutOpen(out _device, WaveMapper, format, 0, 0, 0) != 0)
                {
                    _device = 0;
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(format); }
            _samples = Marshal.AllocHGlobal(dataLength);
            Marshal.Copy(wav, dataOffset, _samples, dataLength);
            _header = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = _samples, BufferLength = (uint)dataLength }, _header, false);
            if (waveOutPrepareHeader(_device, _header, HeaderSize) == 0 && waveOutWrite(_device, _header, HeaderSize) == 0) return true;
            Release();
            return false;
        }
    }

    private static void Release()
    {
        if (_device != 0)
        {
            waveOutReset(_device);
            if (_header != 0) waveOutUnprepareHeader(_device, _header, HeaderSize);
            waveOutClose(_device);
            _device = 0;
        }
        if (_header != 0) Marshal.FreeHGlobal(_header);
        if (_samples != 0) Marshal.FreeHGlobal(_samples);
        _header = _samples = 0;
    }

    /// <summary>WAVEHDR.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength, BytesRecorded;
        public nint User;
        public uint Flags, Loops;
        public nint Next, Reserved;
    }

    [LibraryImport("winmm.dll")]
    private static partial int waveOutOpen(out nint device, uint deviceId, nint format, nint callback, nint instance, uint flags);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutPrepareHeader(nint device, nint header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutUnprepareHeader(nint device, nint header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutWrite(nint device, nint header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutReset(nint device);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutClose(nint device);
}
