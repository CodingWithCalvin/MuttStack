namespace Scout.Core;

/// <summary>A bounded consecutive assignment, distinct from a disc play-all compilation.</summary>
public static class EpisodeRange
{
    public const int Maximum = 4;
    public static bool Valid(IReadOnlyList<Episode> episodes) => episodes.Count is >= 1 and <= Maximum &&
        episodes.Select(e=>e.Id).Distinct().Count()==episodes.Count &&
        episodes.Select((e,i)=>e.Season==episodes[0].Season && e.Number==episodes[0].Number+i).All(x=>x);
    public static void Require(IReadOnlyList<Episode> episodes) {
        if(!Valid(episodes))throw new ArgumentException("Choose one to four distinct consecutive episodes from one season, in playback order.");
    }
}
