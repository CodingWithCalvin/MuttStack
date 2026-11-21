using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Scout.SubtitleBenchmark")]
[assembly:InternalsVisibleTo("Scout.Tests")]
namespace Scout.Core.Subtitles;
internal sealed record Reference(string Id,string Series,string Name,string Url,TranscriptIndex Index);
internal sealed record MappedReference(Reference Reference,string Archive,int Season,SubtitleLabel? Label,ProviderLink? Link,bool MappingBlocked=false);
public sealed record SubtitleReference(string Id,string Name,string Url,string Archive,int Season,List<Cue> Cues);
public sealed record SubtitleReferenceSet(SeriesIdentity Series,List<SubtitleReference> References,string Note="");
public interface ISubtitleReferenceSource {
 Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct);
}
