using FaceRecognizer.Contracts;
using FaceRecognizer.Database.Storage;

namespace FaceRecognizer.Database;

/// <summary>Engangsimport av eldre referansebilder fra mappestrukturen <c>mappe/&lt;navn&gt;/*.jpg</c>.</summary>
public static class LegacyImport
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    /// <summary>
    /// Importerer bildene uten embedding. Allerede importerte bilder hoppes over. Embeddings lages ved reindeksering i Admin.
    /// </summary>
    /// <param name="store">Lageret.</param>
    /// <param name="folder">Rotmappen.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <returns>Antall nye bilder.</returns>
    public static async Task<int> RunAsync(
        EmbeddingStore store, string folder, ILogger logger, CancellationToken cancellationToken)
    {
        var imported = 0;
        foreach (var personFolder in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(personFolder);
            if (!PersonNames.IsValid(name))
            {
                logger.LogWarning("Hopper over mappe med ugyldig navn: {Name}", name);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(personFolder)
                         .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                if (await store.ImportImageAsync(name, bytes, cancellationToken))
                {
                    imported++;
                }
            }
        }

        if (imported > 0)
        {
            logger.LogInformation("Importerte {Count} eldre referansebilder. Kjør reindeksering i Admin.", imported);
        }

        return imported;
    }
}
