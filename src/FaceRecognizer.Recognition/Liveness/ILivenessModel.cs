using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Pipeline;

namespace FaceRecognizer.Recognition.Liveness;

/// <summary>
/// Liveness-modell for ett ansikt i ett bilde. Hver modell oppgir hvilke bildetyper den er trent for, slik at en IR-spesifikk modell
/// kan legges til senere uten å endre videokilden, bufferen eller gjenkjenningen.
/// </summary>
public interface ILivenessModel
{
    /// <summary>Navn på modellen, brukes i logg.</summary>
    string Name { get; }

    /// <summary>Terskel (0-1) per bilde. Er modellens egen kalibrerte verdi, ikke en sannsynlighet.</summary>
    float MinScore { get; }

    /// <summary>Om modellen er trent for og kan brukes på denne bildetypen.</summary>
    /// <param name="mode">Bildetype.</param>
    bool Supports(ImageMode mode);

    /// <summary>Beregner "ekte"-score (0-1) for ett ansikt. Kalles kun for bildetyper modellen støtter.</summary>
    /// <param name="frame">Bildet.</param>
    /// <param name="box">Ansiktsboksen i bildet (fra ansiktssporet).</param>
    float ScoreFace(Frame frame, FaceBox box);
}
