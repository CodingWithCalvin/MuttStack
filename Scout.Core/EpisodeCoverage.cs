namespace Scout.Core;

/// <summary>Episode completeness is independent of whether its wider story has ended.</summary>
public static class EpisodeCoverage
{
    // Read earlier saved responses without rewriting their evidence. New requests use episode terminology.
    public static string Content(string value)=>value switch {
        "complete_story"=>"complete_episode", "partial_story"=>"incomplete_episode", _=>value
    };
    public static string Scope(string value)=>value switch {
        "entire_story"=>"entire_episode", "one_of_multiple_stories"=>"one_of_separate_episodes",
        "partial_story"=>"incomplete_episode", _=>value
    };
    public static bool IsCombinedScope(string value)=>value is "one_of_separate_episodes" or "one_of_continuous_parts";
    public static bool AreNumberedParts(IReadOnlyList<Episode> episodes) {
        if(episodes.Count!=2||episodes[0].Id==episodes[1].Id)return false;
        var ordered=episodes.OrderBy(e=>e.Number).ToArray();
        var first=DescriptionMapping.Part(ordered[0].Title);var second=DescriptionMapping.Part(ordered[1].Title);
        return first is {} a&&second is {} b&&a.Title.Length>0&&a.Title==b.Title&&b.Part==a.Part+1&&
            ordered[0].Season==ordered[1].Season&&ordered[1].Number==ordered[0].Number+1;
    }
}
