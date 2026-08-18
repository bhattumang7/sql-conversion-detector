namespace SilentScan.Core.Rules;

/// <summary>
/// Pure decision for the BETWEEN end-of-period temporal-boundary defect, extracted out of
/// <c>NonSargablePredicateScanner</c>'s visitor (docs/detection-checklist.md "Engineering debt" -
/// separating rule decisions from ScriptDom traversal mechanics). Fires only when the tested
/// column's own declared fractional-seconds scale (TIME/DATETIME2/DATETIMEOFFSET) exceeds the
/// BETWEEN upper bound literal's own fractional-second digit count - the exact, oracle-confirmed
/// mechanism by which rows in the precision gap are silently excluded. Recognizing the shape
/// (a BETWEEN whose first side is a column and third side a string literal) and resolving the
/// column's own catalog scale stay the caller's own concern; this only decides what those
/// already-resolved facts mean.
/// </summary>
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
