using FaceAiSharp;
using FaceAiSharp.Extensions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecognizer.Recognition;

/// <summary>Innstillinger for ansiktsmatching.</summary>
public sealed class FaceMatchOptions
{
    /// <summary>Mappe med referansebilder, én undermappe per person.</summary>
    public string ReferenceFolder { get; set; } = "/data/faces";

    /// <summary>Minste likhet (0-1) som kreves for match. Må kalibreres mot egne bilder.</summary>
    public float Threshold { get; set; } = 0.98f;
}

/// <summary>Resultatet av en sammenligning mot referansebiblioteket.</summary>
/// <param name="FaceFound">Om et ansikt ble funnet i bildet.</param>
/// <param name="PersonName">Beste match, uavhengig av terskel.</param>
/// <param name="Score">Beste score (0-1).</param>
/// <param name="IsMatch">Om scoren oppfyller terskelen.</param>
public sealed record FaceMatchResult(bool FaceFound, string? PersonName, float Score, bool IsMatch);

/// <summary>Sammenligner ansikt i et bilde mot innlastede referanseansikter.</summary>
public sealed class FaceMatcher
{
    private readonly IFaceDetectorWithLandmarks _detector;
    private readonly IFaceEmbeddingsGenerator _embedder;
    private readonly FaceMatchOptions _options;
    private readonly List<(string Name, float[] Embedding)> _references = [];

    /// <summary>Oppretter en matcher.</summary>
    public FaceMatcher(
        IFaceDetectorWithLandmarks detector,
        IFaceEmbeddingsGenerator embedder,
        FaceMatchOptions options)
    {
        _detector = detector;
        _embedder = embedder;
        _options = options;
    }

    /// <summary>Antall lastede referanseansikter.</summary>
    public int ReferenceCount => _references.Count;

    /// <summary>Laster referansebilder fra <see cref="FaceMatchOptions.ReferenceFolder"/>.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task LoadReferencesAsync(CancellationToken cancellationToken = default)
    {
        _references.Clear();
        if (!Directory.Exists(_options.ReferenceFolder))
        {
            return;
        }

        foreach (var personDir in Directory.EnumerateDirectories(_options.ReferenceFolder))
        {
            var name = Path.GetFileName(personDir);
            foreach (var file in Directory.EnumerateFiles(personDir))
            {
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                var embedding = TryEmbed(bytes);
                if (embedding is not null)
                {
                    _references.Add((name, embedding));
                }
            }
        }
    }

    /// <summary>Legger til en referanse direkte (brukes bl.a. i tester).</summary>
    public void AddReference(string name, byte[] imageBytes)
    {
        var embedding = TryEmbed(imageBytes)
            ?? throw new ArgumentException("Fant ikke ansikt i referansebildet.", nameof(imageBytes));
        _references.Add((name, embedding));
    }

    /// <summary>Finner største ansikt i bildet og sammenligner det mot referansene.</summary>
    /// <param name="imageBytes">Bilde (JPEG/PNG) fra kameraet.</param>
    public FaceMatchResult Match(byte[] imageBytes)
    {
        var embedding = TryEmbed(imageBytes);
        if (embedding is null)
        {
            return new FaceMatchResult(false, null, 0f, false);
        }

        string? bestName = null;
        var bestScore = float.MinValue;
        foreach (var (name, reference) in _references)
        {
            var score = embedding.Dot(reference);
            if (score > bestScore)
            {
                bestScore = score;
                bestName = name;
            }
        }

        if (bestName is null)
        {
            return new FaceMatchResult(true, null, 0f, false);
        }

        return new FaceMatchResult(true, bestName, bestScore, bestScore >= _options.Threshold);
    }

    private float[]? TryEmbed(byte[] imageBytes)
    {
        using var image = Image.Load<Rgb24>(imageBytes);
        var faces = _detector.DetectFaces(image);
        var largest = faces
            .Where(f => f.Landmarks is not null)
            .OrderByDescending(f => f.Box.Width * f.Box.Height)
            .FirstOrDefault();
        if (largest.Landmarks is null)
        {
            return null;
        }

        _embedder.AlignFaceUsingLandmarks(image, largest.Landmarks);
        return _embedder.GenerateEmbedding(image);
    }
}
