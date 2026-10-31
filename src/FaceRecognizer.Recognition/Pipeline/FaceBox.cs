namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>Ansiktsboks i pikselkoordinater i analysebildet.</summary>
/// <param name="X">Venstre kant.</param>
/// <param name="Y">Øvre kant.</param>
/// <param name="Width">Bredde.</param>
/// <param name="Height">Høyde.</param>
public readonly record struct FaceBox(float X, float Y, float Width, float Height)
{
    /// <summary>Høyre kant.</summary>
    public float Right => X + Width;

    /// <summary>Nedre kant.</summary>
    public float Bottom => Y + Height;

    /// <summary>Areal i piksler.</summary>
    public float Area => Width * Height;

    /// <summary>Intersection over union mot en annen boks (0-1).</summary>
    /// <param name="other">Boksen det sammenlignes med.</param>
    public float Iou(FaceBox other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        if (width <= 0 || height <= 0)
        {
            return 0f;
        }

        var intersection = width * height;
        return intersection / (Area + other.Area - intersection);
    }

    /// <summary>Boksen skalert rundt origo, brukes når deteksjon er gjort på et nedskalert bilde.</summary>
    /// <param name="factor">Skaleringsfaktor.</param>
    public FaceBox Scale(float factor) => new(X * factor, Y * factor, Width * factor, Height * factor);
}
