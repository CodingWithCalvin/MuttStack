using System.Text.RegularExpressions;

namespace Scout.Core;

internal static class NamingTitles
{
    // Only a consecutive, explicitly numbered 1/2 pair shares a filename title.
    // Provider titles and assignment identities remain untouched.
    public static IReadOnlyList<string> For(IReadOnlyList<Episode> episodes)
    {
        if (episodes.Count == 2 && episodes[0].Season == episodes[1].Season && episodes[1].Number == episodes[0].Number + 1)
        {
            var first = Part(episodes[0].Title);
            var second = Part(episodes[1].Title);
            if (first is { Number: 1 } && second is { Number: 2 } &&
                string.Equals(Key(first.Value.Title), Key(second.Value.Title), StringComparison.OrdinalIgnoreCase))
                return [first.Value.Title];
        }
        return episodes.Select(e => e.Title).ToList();
    }

    private static string Key(string title) => Regex.Replace(title.Normalize(), @"\s+", " ");
    private static (string Title, int Number)? Part(string title)
    {
        var match = Regex.Match(title.Trim(), @"^(?<title>.+?)(?:\s*[-:–—]\s*|\s+)(?:\((?:Part\s+)?(?<n>[12])(?:\s+of\s+2)?\)|Part\s+(?<n>[12])(?:\s+of\s+2)?|(?<n>[12]))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? (match.Groups["title"].Value.TrimEnd(), int.Parse(match.Groups["n"].Value)) : null;
    }
}
