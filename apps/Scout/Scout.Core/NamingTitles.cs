using System.Text.RegularExpressions;

namespace Scout.Core;

internal static class NamingTitles
{
    // Only consecutive explicit parts starting at 1 share a filename title.
    // Provider titles and assignment identities remain untouched.
    public static IReadOnlyList<string> For(IReadOnlyList<Episode> episodes)
    {
        if (episodes.Count > 1 && EpisodeRange.Valid(episodes))
        {
            var first = Part(episodes[0].Title);
            if (first is { Number: 1 } && episodes.Select((episode,index)=>(Part:Part(episode.Title),Index:index)).All(p=>
                p.Part is {} part && part.Number==p.Index+1 && string.Equals(Key(first.Value.Title),Key(part.Title),StringComparison.OrdinalIgnoreCase)))
                return [first.Value.Title];
        }
        return episodes.Select(e => e.Title).ToList();
    }

    private static string Key(string title) => Regex.Replace(title.Normalize(), @"\s+", " ");
    private static (string Title, int Number)? Part(string title)
    {
        var match = Regex.Match(title.Trim(), @"^(?<title>.+?)(?:\s*[-:–—]\s*|\s+)(?:\((?:Part\s+)?(?<n>[1-4])(?:\s+of\s+[2-4])?\)|Part\s+(?<n>[1-4])(?:\s+of\s+[2-4])?|(?<n>[1-4]))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? (match.Groups["title"].Value.TrimEnd(), int.Parse(match.Groups["n"].Value)) : null;
    }
}
