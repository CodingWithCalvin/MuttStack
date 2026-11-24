using System.Text;
using System.Text.RegularExpressions;

namespace Scout.Core;

public sealed record DialogueSpan(int Id,int Block,string Text);
public sealed record EvidenceSelection(IReadOnlyList<Episode> Episodes,IReadOnlyList<int> Blocks,int TotalBlocks);

/// <summary>Deterministic retrieval from original dialogue; never an episode assignment.</summary>
public static class DialogueEvidence
{
    public const string Revision="original-spans-retrieval-v4-blocks";
    public static List<DialogueSpan> Spans(IReadOnlyList<Block> blocks) {
        var spans=new List<DialogueSpan>();
        foreach(var block in blocks) {
            var text=block.SourceText;int start=0;
            while(start<text.Length) {
                while(start<text.Length&&char.IsWhiteSpace(text[start]))start++;
                if(start==text.Length)break;
                int end=Math.Min(start+170,text.Length);
                if(end<text.Length) {
                    int boundary=text.LastIndexOf(' ',end-1,end-start);
                    if(boundary>start)end=boundary;
                    else {int next=text.IndexOf(' ',end);end=next<0?text.Length:next;}
                }
                spans.Add(new(spans.Count,block.Index,text[start..end]));start=end;
            }
        }
        return spans;
    }
    public static string Citation(IReadOnlyList<DialogueSpan> spans,int id,IReadOnlyList<Block> blocks) {
        var span=spans[id];
        bool shortFragment=Regex.Replace(span.Text.Trim(),@"\s+"," ").Length<12||Regex.Matches(span.Text,@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*").Count<3;
        string quote=span.Text;
        if(shortFragment&&id>0&&spans[id-1].Block==span.Block) {
            // A source pointer can land in the short tail of a split block. Retrieve
            // surrounding original words at that exact source location. This does not
            // relax free-form quote validation, cross blocks, or change model verdicts.
            quote=spans[id-1].Text+" "+span.Text;
            while(quote.Length>180) {int cut=quote.IndexOf(' ');if(cut<0)break;quote=quote[(cut+1)..];}
        }
        GroundedQuote.Resolve(blocks,span.Block,quote);
        return quote;
    }
    public static string Text(IReadOnlyList<DialogueSpan> spans,IEnumerable<int>? selected=null) {
        var include=selected?.ToHashSet();var text=new StringBuilder();int previous=-1;
        foreach(var group in spans.Where(s=>include==null||include.Contains(s.Block)).GroupBy(s=>s.Block)) {
            if(include!=null&&group.Key>previous+1)text.AppendLine("[... omitted original dialogue ...]");
            text.Append("Original block b").Append(group.Key).AppendLine(":");
            foreach(var span in group)text.Append("[s").Append(span.Id).Append("] ").Append(span.Text).Append(' ');
            text.AppendLine().AppendLine();previous=group.Key;
        }
        if(include!=null&&spans.Count>0&&previous<spans[^1].Block)text.AppendLine("[... omitted original dialogue ...]");
        return text.ToString();
    }
    public static EvidenceSelection Retrieve(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> catalog) {
        var blocks=DialogueMatcher.Blocks(cues);var ids=new List<int>();
        void Add(IEnumerable<Episode> values){foreach(var episode in values)if(!ids.Contains(episode.Id))ids.Add(episode.Id);}
        Add(DialogueMatcher.Rank(cues,catalog));
        // Search each temporal third as well as the whole recording. Closing stories
        // and an episode with different vocabulary must remain discoverable.
        for(int part=0;part<3;part++)Add(DialogueMatcher.Rank(cues.Skip(cues.Count*part/3).Take(cues.Count*(part+1)/3-cues.Count*part/3).ToArray(),catalog).Take(2));
        if(ids.Count==0)Add(catalog.Take(10));
        var episodes=ids.Take(11).Select(id=>catalog.Single(e=>e.Id==id)).ToArray();
        var words=blocks.Select(b=>Words(b.SourceText)).ToArray();
        var df=words.SelectMany(w=>w).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>g.Count());
        var chosen=new List<int>();
        void Include(int i){if(i>=0&&i<blocks.Count&&!chosen.Contains(i))chosen.Add(i);}
        // Opening, closing and distributed context are independent of the proposed title.
        // Never select only matching lines: preserve local context and potential promos.
        for(int i=0;i<8;i++)Include((blocks.Count-1)*i/7);
        Include(1);for(int i=2;i<=4;i++)Include(blocks.Count-i);
        var matches=new List<int>();
        foreach(var episode in episodes) {
            if(string.IsNullOrWhiteSpace(episode.Overview))continue;
            foreach(var statement in GroundedVerification.Statements(episode)) {
                var query=Words(statement);
                var hits=blocks.Select((b,i)=>(i,score:words[i].Intersect(query).Sum(w=>Math.Log(1.0+blocks.Count/(double)df[w]))/Math.Sqrt(Math.Max(1,words[i].Count))))
                    .Where(x=>x.score>0).OrderByDescending(x=>x.score).ThenBy(x=>x.i).Take(2);
                foreach(var hit in hits)if(!matches.Contains(hit.i))matches.Add(hit.i);
            }
        }
        // Keep a hit with its neighbours as a unit; omitted gaps are marked in Text.
        int size=0;var selected=new List<int>();
        void Group(IEnumerable<int> group) {
            var add=group.Where(i=>i>=0&&i<blocks.Count&&!selected.Contains(i)).Distinct().ToArray();
            int length=add.Sum(i=>blocks[i].SourceText.Length);
            if(selected.Count+add.Length>40||size+length>14000)return;
            selected.AddRange(add);size+=length;
        }
        foreach(var index in chosen)Group([index]);
        foreach(var index in matches)Group([index-1,index,index+1]);
        return new(episodes,selected.Order().ToArray(),blocks.Count);
    }
    static HashSet<string> Words(string text)=>Regex.Matches(text.ToLowerInvariant(),@"[\p{L}]{3,}")
        .Select(m=>m.Value).Where(w=>!Stop.Contains(w)).ToHashSet();
    static readonly HashSet<string> Stop="the and that this with from they their them have has had you your for are was were but not into then when what who she his her him our out all can will about there".Split(' ').ToHashSet();
}
