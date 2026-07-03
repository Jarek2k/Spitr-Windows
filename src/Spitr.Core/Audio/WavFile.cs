using System.Buffers.Binary;

namespace Spitr.Core.Audio;

/// <summary>
/// Minimaler WAV-Reader für 16-bit-PCM-Mono (das Format des Test-Fixtures und
/// des Selftest-Inputs). Bewusst ohne NAudio-Abhängigkeit — 40 Zeilen
/// Chunk-Scanning reichen, und der --selftest im publizierten Exe braucht
/// denselben Reader.
/// </summary>
public static class WavFile
{
    public static AudioBuffer ReadMono16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException($"Keine WAV-Datei: {path}");
        }

        int sampleRate = 0;
        short channels = 0, bitsPerSample = 0;
        float[]? samples = null;

        // Chunks scannen — afconvert/ffmpeg schieben gern LIST/fact zwischen fmt und data.
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var body = offset + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels != 1 || bitsPerSample != 16)
                {
                    throw new InvalidDataException(
                        $"Erwartet 16-bit PCM mono, gefunden {bitsPerSample} bit / {channels} Kanäle: {path}");
                }
                var count = Math.Min(size, bytes.Length - body) / 2;
                samples = new float[count];
                for (var i = 0; i < count; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + i * 2, 2)) / 32768f;
                }
            }
            offset = body + size + (size & 1);
        }

        if (sampleRate == 0 || samples is null)
        {
            throw new InvalidDataException($"WAV ohne fmt/data-Chunk: {path}");
        }
        return new AudioBuffer(samples, sampleRate);
    }
}
