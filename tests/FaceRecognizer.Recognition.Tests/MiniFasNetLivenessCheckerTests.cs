namespace FaceRecognizer.Recognition.Tests;

public class MiniFasNetLivenessCheckerTests
{
    [Fact]
    public void Softmax_summerer_til_en_og_beholder_rekkefolge()
    {
        var probs = MiniFasNetLivenessChecker.Softmax([0.1f, 3.0f, -1.0f]);

        Assert.Equal(1f, probs.Sum(), 4);
        Assert.True(probs[1] > probs[0] && probs[0] > probs[2]);
    }

    [Fact]
    public void Utsnitt_begrenses_av_bildet_og_skalering()
    {
        // 100x100 ansikt midt i et 640x480-bilde med maks skala 2.7 gir 270x270.
        var rect = MiniFasNetLivenessChecker.CalculateCropRectangle(640, 480, 270, 190, 100, 100, 2.7f);

        Assert.Equal(185, rect.X);
        Assert.Equal(105, rect.Y);
        Assert.Equal(271, rect.Width);
        Assert.Equal(271, rect.Height);

        // Ansikt som fyller bildet kan ikke skaleres utover bildekantene.
        var full = MiniFasNetLivenessChecker.CalculateCropRectangle(640, 480, 0, 0, 640, 480, 4.0f);
        Assert.True(full.Right <= 640 && full.Bottom <= 480 && full.X >= 0 && full.Y >= 0);
    }
}
