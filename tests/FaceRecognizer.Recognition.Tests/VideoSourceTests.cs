using System.Buffers;
using FaceRecognizer.Recognition.Configuration;
using FaceRecognizer.Recognition.Pipeline;
using FaceRecognizer.Recognition.Video;
using Xunit;

namespace FaceRecognizer.Recognition.Tests;

public class VideoSourceTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Frame MakeFrame(FrameBuffer buffer, DateTimeOffset at, byte fill, long? generation = null)
    {
        var pixels = ArrayPool<byte>.Shared.Rent(4 * 4 * 3);
        Array.Fill(pixels, fill);
        return new Frame(buffer.NextFrameId(), generation ?? buffer.Generation, at, 4, 4, pixels);
    }

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(1);

    private static (FrameBuffer Buffer, FakeTime Time) Filled(int count, double stepSeconds = 0.2)
    {
        var time = new FakeTime();
        var buffer = new FrameBuffer(20, time);
        buffer.StartNewSession();
        var start = time.Now;
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(AddResult.Accepted, buffer.Add(MakeFrame(buffer, start + TimeSpan.FromSeconds(i * stepSeconds), (byte)(i + 1))));
        }

        time.Now = start + TimeSpan.FromSeconds((count - 1) * stepSeconds);
        return (buffer, time);
    }

    [Fact]
    public async Task Ppm_leses_med_riktig_stoerrelse_og_piksler()
    {
        byte[] header = "P6\n2 1\n255\n"u8.ToArray();
        byte[] pixels = [1, 2, 3, 4, 5, 6];
        using var stream = new MemoryStream([.. header, .. pixels, .. header, .. pixels]);

        var first = await PpmFrameReader.ReadAsync(stream, CancellationToken.None);
        var second = await PpmFrameReader.ReadAsync(stream, CancellationToken.None);
        var end = await PpmFrameReader.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(2, first!.Value.Width);
        Assert.Equal(1, first.Value.Height);
        Assert.Equal(pixels, first.Value.Buffer.AsSpan(0, 6).ToArray());
        Assert.NotNull(second);
        Assert.Null(end);
    }

    [Fact]
    public async Task Ppm_avkortet_bilde_kaster()
    {
        using var stream = new MemoryStream([.. "P6\n2 1\n255\n"u8.ToArray(), 1, 2, 3]);

        await Assert.ThrowsAsync<InvalidDataException>(() => PpmFrameReader.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Ppm_ugyldig_header_kaster()
    {
        using var stream = new MemoryStream("P5\n2 1\n255\n123456"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(() => PpmFrameReader.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void Ferskt_sammenhengende_vindu_gis_eldst_foerst()
    {
        var (buffer, _) = Filled(12);

        var result = buffer.GetWindow(Window, MaxAge, MaxGap, 8);

        using var window = result.Window!;
        Assert.Equal(12, window.Frames.Count);
        Assert.True(window.Frames.Zip(window.Frames.Skip(1), (a, b) => a.CapturedAt < b.CapturedAt && a.FrameId < b.FrameId).All(x => x));
    }

    [Fact]
    public void Gamle_bilder_gir_stale_frames()
    {
        var (buffer, time) = Filled(12);
        time.Now += TimeSpan.FromSeconds(5);

        var result = buffer.GetWindow(Window, MaxAge, MaxGap, 8);

        Assert.Null(result.Window);
        Assert.Equal("stale_frames", result.Reason);
    }

    [Fact]
    public void Tom_buffer_gir_no_frames()
    {
        var buffer = new FrameBuffer(5, new FakeTime());

        Assert.Equal("no_frames", buffer.GetWindow(Window, MaxAge, MaxGap, 1).Reason);
    }

    [Fact]
    public void For_faa_bilder_avvises()
    {
        var (buffer, _) = Filled(5);

        Assert.Equal("too_few_frames", buffer.GetWindow(Window, MaxAge, MaxGap, 8).Reason);
    }

    [Fact]
    public void Hull_i_stroemmen_avkorter_vinduet_til_bildene_etter_hullet()
    {
        var time = new FakeTime();
        var buffer = new FrameBuffer(30, time);
        buffer.StartNewSession();
        var t = time.Now;
        for (var i = 0; i < 6; i++)
        {
            buffer.Add(MakeFrame(buffer, t + TimeSpan.FromSeconds(i * 0.2), (byte)(i + 1)));
        }

        t += TimeSpan.FromSeconds(1.0 + 2.0);
        for (var i = 0; i < 4; i++)
        {
            buffer.Add(MakeFrame(buffer, t + TimeSpan.FromSeconds(i * 0.2), (byte)(i + 100)));
        }

        time.Now = t + TimeSpan.FromSeconds(0.6);

        var result = buffer.GetWindow(TimeSpan.FromSeconds(10), MaxAge, MaxGap, 1);

        using var window = result.Window!;
        Assert.Equal(4, window.Frames.Count);
    }

    [Fact]
    public void Gjentatt_bilde_forkastes_og_gir_ikke_ny_sekvens()
    {
        var time = new FakeTime();
        var buffer = new FrameBuffer(10, time);
        buffer.StartNewSession();

        var first = buffer.Add(MakeFrame(buffer, time.Now, 7));
        var repeated = buffer.Add(MakeFrame(buffer, time.Now + TimeSpan.FromSeconds(0.2), 7));

        Assert.Equal(AddResult.Accepted, first);
        Assert.Equal(AddResult.Duplicate, repeated);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(1, buffer.DuplicatesRejected);
    }

    [Fact]
    public void Bilde_med_eldre_tidsstempel_avvises()
    {
        var time = new FakeTime();
        var buffer = new FrameBuffer(10, time);
        buffer.StartNewSession();
        buffer.Add(MakeFrame(buffer, time.Now, 1));

        var result = buffer.Add(MakeFrame(buffer, time.Now - TimeSpan.FromSeconds(1), 2));

        Assert.Equal(AddResult.OutOfOrder, result);
    }

    [Fact]
    public void Reconnect_toemmer_bufferen_og_gamle_bilder_kan_ikke_legges_inn()
    {
        var (buffer, time) = Filled(10);
        var oldGeneration = buffer.Generation;

        buffer.StartNewSession();
        var late = buffer.Add(MakeFrame(buffer, time.Now + TimeSpan.FromSeconds(0.2), 99, oldGeneration));

        Assert.Equal(0, buffer.Count);
        Assert.Equal(AddResult.OutOfOrder, late);
        Assert.Equal("no_frames", buffer.GetWindow(Window, MaxAge, MaxGap, 1).Reason);
    }

    [Fact]
    public void Bildene_er_holdt_til_vinduet_frigis_selv_om_bufferen_tommes()
    {
        var (buffer, _) = Filled(10);
        using var window = buffer.GetWindow(Window, MaxAge, MaxGap, 8).Window!;

        buffer.StartNewSession();

        Assert.Equal(10, window.Frames.Count);
        Assert.Equal(16, window.Frames[0].Rgb.Length / 3);
    }

    [Fact]
    public void Frigitt_bilde_kan_ikke_lenger_leses_eller_holdes()
    {
        var buffer = new FrameBuffer(5, new FakeTime());
        buffer.StartNewSession();
        var frame = MakeFrame(buffer, DateTimeOffset.UtcNow, 3);
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Retain());
    }

    [Fact]
    public void Bufferen_forkaster_eldste_bilder_over_kapasitet()
    {
        var time = new FakeTime();
        var buffer = new FrameBuffer(3, time);
        buffer.StartNewSession();
        for (var i = 0; i < 6; i++)
        {
            buffer.Add(MakeFrame(buffer, time.Now + TimeSpan.FromSeconds(i * 0.2), (byte)(i + 1)));
        }

        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Rtsp_adresse_bygges_for_reolink_med_enkodede_tegn()
    {
        var url = RtspStream.BuildUrl(new CameraOptions
        {
            BaseUrl = "https://192.168.1.50",
            Username = "admin",
            Password = "p@ss:w/rd",
            Channel = 0,
            Stream = "main"
        });

        Assert.Equal("rtsp://admin:p%40ss%3Aw%2Frd@192.168.1.50:554/Preview_01_main", url);
        Assert.DoesNotContain("p%40ss", RtspStream.Redact(url));
    }

    [Fact]
    public void Eksplisitt_rtsp_adresse_har_forrang_og_ugyldig_strom_avvises()
    {
        Assert.Equal("rtsp://x/y", RtspStream.BuildUrl(new CameraOptions { RtspUrl = "rtsp://x/y", BaseUrl = "" }));
        Assert.Throws<InvalidOperationException>(() =>
            RtspStream.BuildUrl(new CameraOptions { BaseUrl = "https://cam", Stream = "ext" }));
    }

    [Fact]
    public void Ffmpeg_argumenter_bruker_tcp_select_uten_duplikater_og_ppm()
    {
        var args = RtspStream.BuildArguments("rtsp://cam/stream", new StreamOptions { Fps = 5, MaxWidth = 1920 });

        Assert.Contains("tcp", args);
        Assert.Contains("ppm", args);
        Assert.Contains("vfr", args);
        Assert.Contains(args, a => a.StartsWith("select='isnan(prev_selected_t)+gte(t-prev_selected_t,0.18)'", StringComparison.Ordinal)
            && a.Contains("scale='min(1920,iw)':-2", StringComparison.Ordinal));
                Assert.DoesNotContain("nobuffer", args);
                Assert.DoesNotContain("low_delay", args);
        Assert.Equal("-", args[^1]);
    }
}
