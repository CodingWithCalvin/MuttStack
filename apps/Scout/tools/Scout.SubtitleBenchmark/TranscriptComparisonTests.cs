using System.Text;
using Scout.Core.Subtitles;
static class TranscriptComparisonTests {
 public static void Test() {
  void Check(bool condition,string label){if(!condition)throw new Exception(label);}
  var lines=Enumerable.Range(0,300).Select(i=>$"unique{i} planet{i} captain{i} shuttle{i} rescue{i} dialogue{i}").ToArray();
  var original=TranscriptComparison.Index(lines);var formatted=TranscriptComparison.Index(lines.Select(s=>">> CAPTAIN: "+s.ToUpperInvariant()+"! [MUSIC]"));
  Check(TranscriptComparison.Compare(original,formatted,[]).Score==1,"Case, punctuation, speaker labels and SDH normalization");
  var wrong=TranscriptComparison.Index(Enumerable.Range(0,300).Select(i=>$"different{i} school{i} actor{i} stage{i} comedy{i} costume{i}"));
  Check(TranscriptComparison.Compare(original,wrong,[]).Score==0,"Unrelated dialogue must not match");
  var intro=TranscriptComparison.Index(lines.Take(12));var shortMatch=TranscriptComparison.Compare(original,intro,[]);
  Check(shortMatch.SourceCoverage<.1&&shortMatch.SourceSections==1,"A short recap/promo cannot cover the full recording");
  var edited=TranscriptComparison.Index(lines.Take(120).Concat(lines.Skip(150)));
  Check(TranscriptComparison.Compare(original,edited,[]).Score>.9,"An edit retains substantial textual overlap");
  var combined=TranscriptComparison.Index(lines.Concat(Enumerable.Range(0,300).Select(i=>$"different{i} school{i} actor{i} stage{i} comedy{i} costume{i}")));
  var part=TranscriptComparison.Compare(combined,original,[]);
  Check(part.SourceCoverage<.6&&part.ReferenceCoverage==1,"Combined-file coverage must expose the unmatched portion");
  var common=TranscriptComparison.Common([original,formatted,original,wrong]);
  Check(TranscriptComparison.Compare(original,formatted,common).SharedPhrases==0,"Repeated corpus phrases can be excluded");
  Console.WriteLine("6 subtitle-comparison checks passed.");
 }
}
