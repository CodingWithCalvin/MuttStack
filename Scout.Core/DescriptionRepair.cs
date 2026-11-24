using System.Text.RegularExpressions;

namespace Scout.Core;

/// <summary>Detects misplaced summaries; title alignment chooses metadata, never a media assignment.</summary>
public static class DescriptionRepair
{
    public static string ValidationKey(IReadOnlyList<Episode> catalog)=>CollectionResolver.Hash("misplaced-description-v1|"+
        System.Text.Json.JsonSerializer.Serialize(catalog.Select(e=>new{e.Id,e.Season,e.Number,e.Title,e.Overview})));
    static readonly HashSet<string> Stop="the and that this with from they their them have has had you your for are was were but not into then when what who she his her him our out all can will about there".Split(' ').ToHashSet();
    static HashSet<string> Words(string text)=>Regex.Matches(text.ToLowerInvariant(),@"[\p{L}]{3,}").Select(m=>m.Value).Where(w=>!Stop.Contains(w)).ToHashSet();
    static (int Shared,double Ratio) Similarity(string a,string b) {
        var x=Words(a);var y=Words(b);int shared=x.Intersect(y).Count();
        return (shared,shared/(double)Math.Max(1,Math.Min(x.Count,y.Count)));
    }
    public static bool Suspect(Episode episode,IReadOnlyList<Episode> catalog)=>episode.OriginalDescription==null&&catalog.Any(other=>
        other.Id!=episode.Id&&DescriptionMapping.Key(other.Title)!=DescriptionMapping.Key(episode.Title)&&
        !EpisodeCoverage.AreNumberedParts([episode,other])&&Similarity(episode.Overview,other.Overview) is {Shared:>=6,Ratio:>=.45});

    public static Episode? Replacement(Episode episode,IReadOnlyList<Episode> primary,IReadOnlyList<Episode> supplemental) {
        string key=DescriptionMapping.Key(episode.Title);
        if(key.Length==0||primary.Count(e=>DescriptionMapping.Key(e.Title)==key)!=1)return null;
        var own=supplemental.Where(e=>DescriptionMapping.Key(e.Title)==key&&!string.IsNullOrWhiteSpace(e.Overview)).ToArray();
        if(own.Length!=1)return null;
        // Numbering may differ between providers. Exact unique titles are allowed
        // only AFTER the caller verifies the show's external identity. Require the
        // saved synopsis to fit a different uniquely mapped story substantially better.
        double ownScore=Similarity(episode.Overview,own[0].Overview).Ratio;
        return supplemental.Any(other=>other.Id!=own[0].Id&&DescriptionMapping.Key(other.Title)!=key&&
            supplemental.Count(e=>DescriptionMapping.Key(e.Title)==DescriptionMapping.Key(other.Title))==1&&
            primary.Count(e=>DescriptionMapping.Key(e.Title)==DescriptionMapping.Key(other.Title))==1&&
            !EpisodeCoverage.AreNumberedParts([own[0],other])&&
            Similarity(episode.Overview,other.Overview) is var score&&score.Shared>=6&&score.Ratio>=.45&&score.Ratio>=ownScore+.25)?own[0]:null;
    }
}
