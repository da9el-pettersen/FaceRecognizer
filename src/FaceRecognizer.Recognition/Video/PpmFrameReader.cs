using System.Buffers;

namespace FaceRecognizer.Recognition.Video;

/// <summary>Et rått, dekodet bilde lest fra ffmpeg. Mottakeren eier <see cref="Buffer"/> og må levere den tilbake til <see cref="ArrayPool{T}.Shared"/>.</summary>
/// <param name="Width">Bredde i piksler.</param>
/// <param name="Height">Høyde i piksler.</param>
/// <param name="Buffer">RGB-data (minst bredde × høyde × 3 byte).</param>
public readonly record struct RawFrame(int Width, int Height, byte[] Buffer);

/// <summary>Leser en strøm av PPM-bilder (<c>P6</c>, 8 bit) slik ffmpeg skriver dem med <c>image2pipe</c>. Headeren oppgir størrelsen, så ffprobe er unødvendig.</summary>
public static class PpmFrameReader
{
    private const int MaxDimension = 8192;

    /// <summary>Leser neste bilde, eller <c>null</c> når strømmen er slutt mellom to bilder.</summary>
    /// <param name="stream">Strømmen med PPM-bilder.</param>
    /// <param name="cancellationToken">Avbrytelse.</param>
    /// <exception cref="InvalidDataException">Headeren er ugyldig, eller strømmen sluttet midt i et bilde.</exception>
    public static async Task<RawFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var magic = new byte[2];
        if (await stream.ReadAsync(magic.AsMemory(0, 1), cancellationToken) == 0)
        {
            return null;
        }

        await ReadExactAsync(stream, magic.AsMemory(1), cancellationToken);
        if (magic[0] != (byte)'P' || magic[1] != (byte)'6')
        {
            throw new InvalidDataException("Ugyldig PPM-header (forventet P6).");
        }

        var width = await ReadNumberAsync(stream, cancellationToken);
        var height = await ReadNumberAsync(stream, cancellationToken);
        var maxValue = await ReadNumberAsync(stream, cancellationToken);
        if (maxValue != 255 || width is <= 0 or > MaxDimension || height is <= 0 or > MaxDimension)
        {
            throw new InvalidDataException($"Ugyldig PPM-header ({width}x{height}, maks {maxValue}).");
        }

        var length = width * height * 3;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactAsync(stream, buffer.AsMemory(0, length), cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return new RawFrame(width, height, buffer);
    }

    // Leser ett tall og forbruker nøyaktig ett skilletegn etter det (slik PPM krever etter siste tall).
    private static async Task<int> ReadNumberAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        var value = 0;
        var digits = 0;
        while (true)
        {
            await ReadExactAsync(stream, one, cancellationToken);
            var c = one[0];
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                value = value * 10 + (c - '0');
                if (++digits > 6)
                {
                    throw new InvalidDataException("Ugyldig tall i PPM-header.");
                }
            }
            else if (digits > 0)
            {
                return value;
            }
            else if (c is not ((byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
            {
                throw new InvalidDataException("Ugyldig tegn i PPM-header.");
            }
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            var read = await stream.ReadAsync(destination, cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("Strømmen sluttet midt i et bilde.");
            }

            destination = destination[read..];
        }
    }
}
