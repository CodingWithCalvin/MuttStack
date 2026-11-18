using System.Text.RegularExpressions;
namespace Scout.Core;

// The selected source remains the move boundary. A season folder consumes the
// template through its season component; it never widens the Scout to its parent.
internal sealed record SeasonFolderScope(int Season,int Component)
{
    public static SeasonFolderScope? Detect(string root,string series,NamingOptions naming,IEnumerable<Episode> episodes,SeriesIdentity? identity)
    {
        var name=Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        int component=naming.PathTemplate is null?0:NamingDsl.SeasonFolderIndex(naming.PathTemplate);
        var match=Regex.Match(name,@"^(?:Season[ ._-]*|S)(\d{1,3})$",RegexOptions.IgnoreCase);
        if(match.Success)return new(int.Parse(match.Groups[1].Value),component);
        if(name.Equals("Specials",StringComparison.OrdinalIgnoreCase))return new(0,component);
        if(component<0)return null;
        var seasons=episodes.Where(e=>string.Equals(Parts(naming.Folder(series,e,identity)).ElementAtOrDefault(component),name,StringComparison.OrdinalIgnoreCase))
            .Select(e=>e.Season).Distinct().ToArray();
        return seasons.Length==1?new(seasons[0],component):null;
    }
    private static string[] Parts(string folder)=>folder.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    public string RelativeFolder(string folder)=>string.Join(Path.DirectorySeparatorChar,Parts(folder).Skip(Component+1));
    public string? BlockReason(Episode episode)=>episode.Season!=Season
        ?$"Matched {episode.Code} is outside source season {Season:00}. Use a Scout rooted at the show folder to move between seasons."
        :Component<0?"The path template has multiple season folders. Use one season component when the source is already a season folder.":null;
}
