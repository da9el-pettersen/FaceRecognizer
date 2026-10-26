using System.Numerics;

namespace FaceRecognizer.Recognition.Engines;

/// <summary>SIMD-baserte vektoroperasjoner for ansiktsembeddings.</summary>
public static class EmbeddingMath
{
    /// <summary>Skalerer vektoren til lengde 1 (L2-norm) på stedet.</summary>
    /// <param name="vector">Vektoren som normaliseres.</param>
    /// <returns><c>false</c> hvis vektoren er tom, har lengde 0 eller inneholder NaN/uendelig (vektoren endres da ikke).</returns>
    public static bool Normalize(Span<float> vector)
    {
        if (vector.IsEmpty)
        {
            return false;
        }

        var norm = MathF.Sqrt(Dot(vector, vector));
        if (!float.IsFinite(norm) || norm == 0f)
        {
            return false;
        }

        var scale = 1f / norm;
        var i = 0;
        var width = Vector<float>.Count;
        for (; i <= vector.Length - width; i += width)
        {
            (new Vector<float>(vector[i..]) * scale).CopyTo(vector[i..]);
        }

        for (; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }

        return true;
    }

    /// <summary>Skalarprodukt. For normaliserte vektorer er dette lik cosinus-likhet.</summary>
    /// <param name="a">Første vektor.</param>
    /// <param name="b">Andre vektor, samme lengde som <paramref name="a"/>.</param>
    /// <exception cref="ArgumentException">Vektorene har ulik lengde.</exception>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vektorene må ha lik lengde.", nameof(b));
        }

        var sum = Vector<float>.Zero;
        var i = 0;
        var width = Vector<float>.Count;
        for (; i <= a.Length - width; i += width)
        {
            sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        }

        var result = Vector.Sum(sum);
        for (; i < a.Length; i++)
        {
            result += a[i] * b[i];
        }

        return result;
    }

    /// <summary>Cosinus-likhet for vektorer som ikke nødvendigvis er normalisert.</summary>
    /// <returns>Likhet mellom -1 og 1, eller 0 ved ulik lengde, tom vektor eller nullvektor.</returns>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0f;
        }

        var na = Dot(a, a);
        var nb = Dot(b, b);
        return na == 0f || nb == 0f ? 0f : Dot(a, b) / MathF.Sqrt(na * nb);
    }
}
