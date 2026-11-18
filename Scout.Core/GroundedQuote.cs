using System.Text.RegularExpressions;
namespace Scout.Core;

public sealed record GroundedQuote(int Block,int EndBlock,string SourceText)
{
    /// <summary>Resolves exact consecutive words, allowing subtitle punctuation and one adjacent block boundary.</summary>
    public static GroundedQuote Resolve(IReadOnlyList<Block> blocks,int block,string quote)
    {
        if(block<0||block>=blocks.Count||Regex.Replace(quote.Trim(),@"\s+"," ").Length is <12 or >180)
            throw new InvalidDataException("Invalid grounded quotation.");
        const string pattern=@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*";
        var words=Regex.Matches(quote,pattern);if(words.Count<3)throw new InvalidDataException("A grounded quote needs a meaningful original phrase.");
        string Canonical(string word)=>word.Replace('’','\'').ToUpperInvariant();
        var current=blocks[block].SourceText;var text=current+(block+1<blocks.Count?" "+blocks[block+1].SourceText:"");var source=Regex.Matches(text,pattern);
        for(int start=0;start+words.Count<=source.Count&&source[start].Index<current.Length;start++) {
            if(!Enumerable.Range(0,words.Count).All(i=>Canonical(source[start+i].Value)==Canonical(words[i].Value)))continue;
            int end=source[start+words.Count-1].Index+source[start+words.Count-1].Length;
            return new(block,end>current.Length?block+1:block,text[source[start].Index..end]);
        }
        throw new InvalidDataException("Grounded evidence must contain the same consecutive words in its cited dialogue block or its immediate continuation. No words may be added, skipped or reordered.");
    }
}
