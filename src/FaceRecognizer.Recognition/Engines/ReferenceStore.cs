using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FaceRecognizer.Recognition;

/// <summary>
/// Lokalt, varig referanselager: <c>&lt;mappe&gt;/&lt;person-ID&gt;/&lt;bilde&gt;</c>.
/// Delt av alle ansiktsmotorer, så registrerte personer overlever restart.
/// </summary>
public static partial class ReferenceStore
{
    [GeneratedRegex(@"^[\p{L}\p{N}_ -]{1,64}$")]
    private static partial Regex PersonIdPattern();

    /// <summary>Om person-ID-en er trygg å bruke som mappenavn.</summary>
    public static bool IsValidPersonId(string personId) => PersonIdPattern().IsMatch(personId);

    /// <summary>Lagrer et referansebilde og returnerer stien.</summary>
    /// <param name="folder">Rotmappe for referanser.</param>
    /// <param name="personId">Person-ID.</param>
    /// <param name="image">Bildedata.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public static async Task<string> SaveAsync(
        string folder, string personId, byte[] image, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(folder, personId);
        Directory.CreateDirectory(directory);
        var fileName = Convert.ToHexString(SHA256.HashData(image))[..16].ToLowerInvariant() + ".jpg";
        var path = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(path, image, cancellationToken);
        return path;
    }

    /// <summary>Lister alle referansebilder med person-ID.</summary>
    /// <param name="folder">Rotmappe for referanser.</param>
    public static IEnumerable<(string PersonId, string Path)> Enumerate(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var personDir in Directory.EnumerateDirectories(folder))
        {
            var personId = Path.GetFileName(personDir);
            if (!IsValidPersonId(personId))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(personDir))
            {
                yield return (personId, file);
            }
        }
    }
}
