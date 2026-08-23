namespace SilentScan.Core.Rules;

public static class TemporalBoundaryClassifier
{
    public static bool HasInsufficientFractionalPrecision(int columnScale, string upperBoundLiteralText, out int literalFractionalDigits)
    {
        literalFractionalDigits = CountFractionalDigits(upperBoundLiteralText);
        return literalFractionalDigits < columnScale;
    }

    private static int CountFractionalDigits(string text)
    {
        var dotIndex = text.LastIndexOf('.');
        if (dotIndex < 0)
        {
            return 0;
        }

        var digits = 0;
        for (var i = dotIndex + 1; i < text.Length && char.IsDigit(text[i]); i++)
        {
            digits++;
        }

        return digits;
    }
}
