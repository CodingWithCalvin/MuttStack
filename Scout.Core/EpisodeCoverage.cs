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
        if(episodes.Count<2)return false;
        var ordered=episodes.OrderBy(e=>e.Number).ToArray();
        if(!EpisodeRange.Valid(ordered))return false;
        var first=DescriptionMapping.Part(ordered[0].Title)??(DescriptionMapping.Key(ordered[0].Title),1);
        return first is {} a && a.Title.Length>0 && ordered.Select((e,i)=>(Part:i==0?first:DescriptionMapping.Part(e.Title),Index:i))
            .All(p=>p.Part is {} part && part.Title==a.Title && part.Part==a.Part+p.Index);
    }
    public static int PartNumber(Episode episode)=>DescriptionMapping.Part(episode.Title)?.Part??1;
    public static bool HasSharedDescription(IReadOnlyList<Episode> episodes)=>AreNumberedParts(episodes)&&
        PartNumber(episodes.OrderBy(e=>e.Number).First())==1&&
        episodes.All(e=>!string.IsNullOrWhiteSpace(e.Overview))&&episodes.Select(e=>System.Text.RegularExpressions.Regex.Replace(e.Overview.Trim(),@"\s+"," ")).Distinct(StringComparer.OrdinalIgnoreCase).Count()==1;
    public static IReadOnlyList<Episode> SharedGroup(IReadOnlyList<Episode> proposed,IReadOnlyList<Episode> catalog) {
        if(proposed.Count==0)return proposed;
        string Base(Episode e)=>DescriptionMapping.Part(e.Title)?.Title??DescriptionMapping.Key(e.Title);
        var first=proposed[0];
        if(proposed.Any(e=>e.Season!=first.Season||Base(e)!=Base(first)))return proposed;
        var group=catalog.Where(e=>e.Season==first.Season&&Base(e)==Base(first)).OrderBy(e=>e.Number).ToArray();
        return HasSharedDescription(group)&&EpisodeRange.Valid(group)?group:proposed;
    }
}
