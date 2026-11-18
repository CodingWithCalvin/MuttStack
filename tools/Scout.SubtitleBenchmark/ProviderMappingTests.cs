using Scout.Core;

static class ProviderMappingTests {
 public static void Run() {
  int checks=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
  Episode[] guide=[new(101,3,11,"Silver Comet"),new(102,3,12,"Golden River"),new(103,2,6,"The Long Journey"),new(104,1,1,"Encounter at Elsewhere (1)"),new(105,1,2,"Encounter at Elsewhere (2)")];
  Check(TitleMapping.Find("Example.S03E12.WEBRip.srt",guide,"r")==null,"Number-only uploads must not map to provider numbers.");
  Check(TitleMapping.Find("11 Silver Comet.en.SDH.srt",guide,"r")?.Episodes.Single().Id==101,"Leading ordinal and subtitle language/accessibility suffixes are formatting, not title text.");
  Check(TitleMapping.Find("Example.S03E12.Silver.Comet.WEBRip.srt",guide,"r")?.Episodes.Single().Id==101,"A unique title maps to provider IDs despite different upload numbering.");
  Check(TitleMapping.Find("01.01-02 Encounter a Elsewhere.srt",guide,"r") is {Episodes.Length:2,Complete:true},"Long multipart title typo requires independently agreeing explicit range.");
  Check(TitleMapping.Find("01.02-03 Encounter a Elsewhere.srt",guide,"r")==null,"A near title with conflicting range must be held.");
  Check(TitleMapping.Find("01.01 Encounter at Elsewhere.srt",guide,"r")==null,"Base title alone cannot expand to two provider episodes.");
  Check(TitleMapping.Find("Example.S02E06.The.Long.Journey.Part1.WEBRip.srt",guide,"r") is {Complete:false},"A subtitle part must not become a complete unsplit provider episode.");
  Check(TitleMapping.Find("Silver.Comet.srt",[new(1,1,1,"Silver Comet"),new(2,2,1,"Silver Comet")],"r")==null,"Duplicate catalog titles must remain ambiguous.");
  string[] Lines(string prefix,int count=180)=>Enumerable.Range(0,count).Select(i=>$"{prefix}{i} alpha{i} planet{i} nebula{i} captain{i} {prefix}end{i}").ToArray();
  var one=TranscriptComparison.Index(Lines("first"));var two=TranscriptComparison.Index(Lines("second"));
  var full=TranscriptComparison.Combine(one,two);
  MappedReference Ref(string id,string name,TranscriptIndex index)=>new(new(id,"Example",name,"https://subdl.com",index),"archive",2,TitleMapping.Label(name),TitleMapping.Find(name,guide,id));
  var anchor=Ref("anchor","Example.S02E06.The.Long.Journey.srt",full);
  var first=Ref("part1","Example.S02E06a.Part1.WEBRip.srt",one);var second=Ref("part2","Example.S02E06b.Part2.WEBRip.srt",two);
  var candidates=ReferenceMatcher.Candidates([first,second,anchor],[]);
  var pair=candidates.Single(c=>c.Parts.Length==2);
  Check(ReferenceMatcher.Candidates([Ref("p1of3","Example.S02E06.Part1.of.3.srt",one),Ref("p2of3","Example.S02E06.Part2.of.3.srt",two),anchor],[]).All(c=>c.Parts.Length==1),"Two parts of a declared three-part reference cannot become a complete pair.");
  Check(pair.Link is {Complete:true}&&pair.Link.Episodes.Single().Id==103,"Two subtitle parts link automatically to one unsplit provider episode.");
  var combined=ReferenceMatcher.Match(full,[pair],[]);
  Check(combined.ProviderMapped&&combined.Best!.OrderedParts&&combined.Best.Parts.All(p=>p.SourceCoverage>.4),"Both substantial ordered parts can qualify.");
  Check(!ReferenceMatcher.Match(TranscriptComparison.Combine(two,one),[pair],[]).TextSupported,"Reversed parts cannot pass chronological coverage.");
  Check(!ReferenceMatcher.Match(TranscriptComparison.Combine(one,TranscriptComparison.Index(Lines("second",12))),[pair],[]).ProviderMapped,"Short next-episode promo must not qualify as a second episode.");
  Check(!ReferenceMatcher.Match(one,[pair],[]).ProviderMapped,"A missing second part is incomplete.");
  Check(!ReferenceMatcher.Match(one,ReferenceMatcher.Candidates([anchor],[]),[]).ProviderMapped,"A half recording cannot pass as a complete episode by matching half of a full-length reference.");
  var duplicateParts=ReferenceMatcher.Candidates([first,second with {Reference=second.Reference with {Index=one}},anchor],[]).Single(c=>c.Parts.Length==2);
  Check(!ReferenceMatcher.Match(one,[duplicateParts],[]).TextSupported,"Duplicated dialogue cannot masquerade as two different parts.");
  var wrong=Ref("wrong","Example.S03E12.Golden.River.srt",one);
  var right=Ref("right","Example.S03E12.Silver.Comet.srt",one);
  Check(!ReferenceMatcher.Match(one,ReferenceMatcher.Candidates([right,wrong],[]),[]).ProviderMapped,"Conflicting title anchors must remain unresolved even for identical reference text.");
  var numeric=Ref("numeric","Example.S03E12.WEBRip.srt",one);
  Check(ReferenceMatcher.Match(one,ReferenceMatcher.Candidates([numeric,right],[]),[]).Best?.Candidate.Link?.Episodes.Single().Id==101,"Dialogue bridge must use anchor provider ID, not upload episode number.");
  var unrelated=TranscriptComparison.Index(Lines("other"));
  Check(!ReferenceMatcher.Match(unrelated,candidates,[]).TextSupported,"Wrong dialogue must remain held.");
  Console.WriteLine($"{checks} provider-mapping and multipart checks passed.");
 }
}
