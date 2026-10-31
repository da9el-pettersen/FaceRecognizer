using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Camera;

namespace FaceRecognizer.Recognition.Liveness;

/// <summary>
/// Avgjør om et bildevindu er farge (RGB) eller IR. Gråtone alene er ikke bevis på IR, så kameraets rapporterte modus brukes
/// som tillegg, og alle bilder i vinduet må være entydige. Er noe usikkert gis <see cref="ImageMode.Unknown"/>, som aldri
/// behandles som farge.
/// </summary>
public static class ImageModeClassifier
{
    /// <summary>Klassifiserer vinduet.</summary>
    /// <param name="chromas">Gjennomsnittlig fargespredning per bilde (0-255).</param>
    /// <param name="reported">Modus kameraet rapporterer (konfigurert, ikke nødvendigvis faktisk).</param>
    /// <param name="monoMaxChroma">Grense for svart/hvitt (på eller under).</param>
    /// <param name="colorMinChroma">Grense for farge (på eller over).</param>
    public static ImageMode Classify(
        IReadOnlyCollection<double> chromas, ReportedDayNight reported, double monoMaxChroma, double colorMinChroma)
    {
        if (chromas.Count == 0)
        {
            return ImageMode.Unknown;
        }

        var allMono = chromas.All(c => c <= monoMaxChroma);
        var allColor = chromas.All(c => c >= colorMinChroma);

        return reported switch
        {
            // Kameraet er tvunget til svart/hvitt: bildene må også se slik ut.
            ReportedDayNight.BlackAndWhite => allMono ? ImageMode.Infrared : ImageMode.Unknown,

            // Kameraet er tvunget til farge: bildene må være tydelig farget (mørke scener kan se grå ut).
            ReportedDayNight.Color => allColor ? ImageMode.Color : ImageMode.Unknown,

            // Auto eller ukjent: bildene avgjør alene, og farge kreves å være tydelig.
            _ => allColor ? ImageMode.Color : allMono ? ImageMode.Infrared : ImageMode.Unknown
        };
    }
}
