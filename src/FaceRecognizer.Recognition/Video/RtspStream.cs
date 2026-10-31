using System.Globalization;
using FaceRecognizer.Recognition.Configuration;

namespace FaceRecognizer.Recognition.Video;

/// <summary>Bygger RTSP-adresse og ffmpeg-argumenter for den delte videokilden.</summary>
public static class RtspStream
{
    /// <summary>
    /// Bygger RTSP-adressen. Bruker <see cref="CameraOptions.RtspUrl"/> hvis satt, ellers Reolink-formatet
    /// <c>rtsp://bruker:passord@vert:554/Preview_01_main</c> (kanalnummeret i adressen er 1-basert).
    /// </summary>
    /// <param name="camera">Kamerainnstillinger.</param>
    /// <exception cref="InvalidOperationException">Verken RTSP-adresse eller kameraadresse er satt.</exception>
    public static string BuildUrl(CameraOptions camera)
    {
        if (!string.IsNullOrWhiteSpace(camera.RtspUrl))
        {
            return camera.RtspUrl;
        }

        if (!Uri.TryCreate(camera.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Camera:BaseUrl eller Camera:RtspUrl må være satt for videostrøm.");
        }

        var stream = camera.Stream.Trim().ToLowerInvariant();
        if (stream is not ("main" or "sub"))
        {
            throw new InvalidOperationException("Camera:Stream må være 'main' eller 'sub'.");
        }

        var credentials = string.IsNullOrEmpty(camera.Username)
            ? ""
            : $"{Uri.EscapeDataString(camera.Username)}:{Uri.EscapeDataString(camera.Password)}@";
        return string.Create(CultureInfo.InvariantCulture,
            $"rtsp://{credentials}{baseUri.Host}:554/Preview_{camera.Channel + 1:00}_{stream}");
    }

    /// <summary>Fjerner brukernavn og passord fra adressen slik at den kan logges.</summary>
    /// <param name="url">RTSP-adresse.</param>
    public static string Redact(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.LastIndexOf('@');
        return scheme >= 0 && at > scheme ? string.Concat(url.AsSpan(0, scheme + 3), "***@", url.AsSpan(at + 1)) : url;
    }

    /// <summary>
    /// Argumentene til ffmpeg: én RTSP-forbindelse over TCP, lav latens, redusert til <see cref="StreamOptions.Fps"/> uten duplikater
    /// (select fremfor fps-filter), skalert til høyst <see cref="StreamOptions.MaxWidth"/> og skrevet som PPM-bilder til stdout.
    /// </summary>
    /// <param name="url">RTSP-adresse.</param>
    /// <param name="stream">Strøminnstillinger.</param>
    public static IReadOnlyList<string> BuildArguments(string url, StreamOptions stream)
    {
        // 10 % slingringsmonn, ellers gir en 10 fps-kilde bare 4 fps ved 5 fps (avrundingsfeil i tidsstempler).
        var interval = (0.9 / stream.Fps).ToString("0.######", CultureInfo.InvariantCulture);
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"select='isnan(prev_selected_t)+gte(t-prev_selected_t,{interval})',scale='min({stream.MaxWidth},iw)':-2");
        var isRtsp = url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase);
        List<string> arguments = ["-hide_banner", "-loglevel", "warning", "-nostdin"];
        if (isRtsp)
        {
            // nobuffer/low_delay er bevisst utelatt: mot Reolink ga de HEVC-dekodingsfeil og tapte bilder.
            arguments.AddRange(["-rtsp_transport", "tcp"]);
        }

        arguments.AddRange(
        [
            "-i", url,
            "-an", "-sn",
            "-vf", filter,
            "-fps_mode", "vfr",
            "-pix_fmt", "rgb24",
            "-c:v", "ppm",
            "-f", "image2pipe",
            "-"
        ]);
        return arguments;
    }
}
