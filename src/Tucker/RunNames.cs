using System.IO;
using System.Text.RegularExpressions;
namespace Tucker;

public static class RunNames
{
    static string Clean(string? text)=>Regex.Replace((text??"").Replace('_',' '),@"\s+"," ").Trim();
    static string FolderLabel(string path)=>Clean(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
    public static string Suggest(IEnumerable<Job> preview,IEnumerable<string> folders,IEnumerable<Handoff> batches)
    {
        var jobs=preview.ToList();var roots=folders.ToList();var files=batches.SelectMany(b=>b.Manifest.Files).ToList();
        var ready=jobs.Where(j=>j.Status!="Already imported").ToList();if(ready.Count>0)jobs=ready;
        var entries=jobs.Count>0
            ?jobs.Select(j=>(Series:Clean(j.Series?.Name),Season:Clean(j.Season),Label:Clean(j.DiscLabel).Length>0?Clean(j.DiscLabel):FolderLabel(Path.GetDirectoryName(j.Source)??j.Source))).ToList()
            :files.Select(f=>(Series:Clean(f.Series?.Name),Season:Clean(f.Season),Label:Clean(f.DiscLabel).Length>0?Clean(f.DiscLabel):FolderLabel(Path.GetDirectoryName(f.Source)??f.Source))).ToList();
        if(jobs.Count==0)entries.AddRange(roots.Select(f=>(Series:"",Season:"",Label:FolderLabel(f))));
        var labels=entries.Select(e=>e.Series.Length>0?e.Series:e.Label).Where(s=>s.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).ToList();
        if(labels.Count==0){if(entries.Count==0)return "";labels.Add("Selected rips");}
        var title=string.Join(" + ",labels.Take(2).Select(s=>s.Length>45?s[..42]+"…":s));
        if(labels.Count>2)title+=$" + {labels.Count-2} more";
        if(entries.All(e=>e.Series.Length>0)&&entries.Select(e=>e.Series).Distinct(StringComparer.OrdinalIgnoreCase).Count()==1&&entries.All(e=>e.Season.Length>0)) {
            var seasons=entries.Select(e=>e.Season).Distinct().OrderBy(s=>int.TryParse(s,out var n)?n:int.MaxValue).ThenBy(s=>s).ToList();
            title+=seasons.Count==1?" · Season "+seasons[0]:seasons.Count<=3?" · Seasons "+string.Join(", ",seasons):$" · {seasons.Count} seasons";
        }
        if(jobs.Count>0)title+=$" · {jobs.Count} {(jobs.Count==1?"file":"files")}";
        else if(roots.Count==0) {var count=files.Select(f=>Doggo.DoggoInbox.SourceKey(f.Source,f.SourceSize,f.SourceModified)).Distinct(StringComparer.OrdinalIgnoreCase).Count();title+=$" · {count} {(count==1?"file":"files")}";}
        return title;
    }
    public static string Unique(string name,IEnumerable<string> existing)
    {
        if(name.Length==0)return name;
        var used=existing.ToHashSet(StringComparer.OrdinalIgnoreCase);var result=name;
        for(int suffix=2;used.Contains(result);suffix++)result=$"{name} ({suffix})";
        return result;
    }
}
