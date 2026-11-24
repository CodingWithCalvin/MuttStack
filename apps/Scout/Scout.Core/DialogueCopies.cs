using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
namespace Scout.Core;

// Exact equality of the complete normalized word sequence is deliberately stricter
// than similar summaries or common episode assignments. Encodings may differ.
internal static class DialogueCopies
{
    private static string? Key(RecordingGroup group) {
        if(group.Status!="suggested"||group.HumanDecision!=null||group.Episodes.Count!=1)return null;
        var keys=new List<string>();
        foreach(var row in group.Rows) {
            if(row["transcription"]!=null)return null; // Generated text alone cannot establish identical recordings.
            if(!row.Bool("full_dialogue")||row["reasoning"].Text("coverage")!="complete"||
                (row["reasoning"].Text("method")!=DialogueMatcher.Version&&!Subtitles.SubtitleResult.Valid(row))||row["reasoning"].Text("dialogue_hash")!=MultipartEvidence.DialogueHash(row))return null;
            var cues=MultipartEvidence.Cues(row);double duration=row["duration"]?.GetValue<double>()??0;
            if(duration<120||cues.Count<30||cues[0].Start>duration*.1||cues[^1].Start<duration*.8)return null;
            var words=Regex.Matches(string.Join(" ",cues.Select(c=>c.Text)).ToLowerInvariant(),@"[\p{L}\p{Nd}]+").Select(m=>m.Value).ToArray();
            if(words.Length<256||words.Distinct().Count()<80)return null;
            keys.Add(CollectionResolver.Hash(string.Join(" ",words)));
        }
        return keys.Distinct().Count()==1?group.Episode!.Id+":"+keys[0]:null;
    }
    public static void Merge(List<RecordingGroup> groups) {
        var keyed=groups.Select(g=>(Group:g,Key:Key(g))).Where(p=>p.Key!=null).GroupBy(p=>p.Key).ToList();
        foreach(var bucket in keyed) {
            var matches=bucket.Select(p=>p.Group).ToList();if(matches.Count<2)continue;
            var rows=matches.SelectMany(g=>g.Rows).ToList();var durations=rows.Select(r=>r["duration"]!.GetValue<double>()).ToArray();
            if(durations.Max()-durations.Min()>Math.Max(3,durations.Max()*.01))continue;
            var first=matches[0];var merged=new RecordingGroup {
                Id=CollectionResolver.Hash(string.Join("\n",rows.Select(r=>r.Text("path")).Order(StringComparer.Ordinal)))[..16],
                Rows=rows,Claims=first.Claims.ToList(),Episodes=first.Episodes.ToList(),Status="suggested",DialogueCopies=true
            };
            int index=groups.IndexOf(first);foreach(var group in matches)groups.Remove(group);groups.Insert(index,merged);
        }
    }
}
