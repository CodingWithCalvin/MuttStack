using System.IO;
namespace Tucker;

public static class RunPlanner
{
    public static List<Job> Plan(string[] folders,IReadOnlyList<Handoff> batches,Options options,bool recursive,string format,string prefix,string filenameFormat,IEnumerable<Job>? existing=null)
    {
        // Inbox metadata takes precedence when the same raw file is also in a selected folder.
        var incoming=batches.SelectMany(b=>b.Plan(options,format,prefix)).GroupBy(QueueManager.SourceKey,StringComparer.OrdinalIgnoreCase).ToList();
        foreach(var group in incoming)if(group.Select(j=>(j.Series,j.Season,j.VolumeNumber,j.DiscNumber,j.DiscLabel,j.Name,j.Encoding!.Output)).Distinct().Count()>1)
            throw new IOException("Inbox batches disagree about the metadata for "+group.First().Source+". Choose the correct batch before creating this run.");
        var jobs=incoming.Select(g=>g.First()).ToList();
        var covered=jobs.Select(QueueManager.SourceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining=Engine.Scan(folders,options.Output,recursive,options.Extension).Where(j=>!covered.Contains(QueueManager.SourceKey(j)));
        jobs.AddRange(FolderNames.PlanFiles(remaining,folders,options,format,filenamePrefix:prefix));
        Naming.Apply(jobs,filenameFormat);
        var names=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach(var job in existing??[])if(job.Encoding is {} encoding) {
            if(!names.TryGetValue(encoding.Output,out var reserved))names[encoding.Output]=reserved=new(StringComparer.OrdinalIgnoreCase);
            reserved.Add(job.Name);
        }
        foreach(var job in jobs) {
            var output=job.Encoding!.Output;
            if(!names.TryGetValue(output,out var reserved))names[output]=reserved=new(StringComparer.OrdinalIgnoreCase);
            job.Name=Engine.Unique(output,job.Name,reserved);
        }
        return jobs;
    }
}
