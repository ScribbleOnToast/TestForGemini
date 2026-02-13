namespace DigitalEye.Helpers;

using System.Text.RegularExpressions;

public static class TextSanitizer
{
    // Matches any punctuation character
    private static readonly Regex PunctuationRegex = new Regex(@"[\p{P}]", RegexOptions.Compiled);

    public static string RemovePunctuation(this string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // Replaces all punctuation with nothing, leaving only spaces and letters/numbers
        return PunctuationRegex.Replace(input, "").Trim();
    }
}