using System.Text;
using Scout.Core.Subtitles;
static class SubtitleTextTests {
 public static void Test() {
  var expected="That’s the captain’s shuttle.";
  if(SubtitleText.Decode(Encoding.UTF8.GetBytes(expected)).Text!=expected)throw new Exception("UTF-8 subtitle decode failed.");
  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
  var legacy=SubtitleText.Decode(Encoding.GetEncoding(1252).GetBytes(expected));
  if(legacy.Text!=expected||legacy.Encoding!="windows-1252")throw new Exception("Windows subtitle decode failed.");
  foreach(var encoding in new Encoding[]{new UnicodeEncoding(false,true,true),new UnicodeEncoding(true,true,true),new UTF32Encoding(false,true,true)})
   if(SubtitleText.Decode(encoding.GetPreamble().Concat(encoding.GetBytes(expected)).ToArray()).Text!=expected)throw new Exception("Unicode BOM was not respected.");
  Console.WriteLine("2 subtitle encoding checks passed (UTF-8/Windows-1252 and Unicode BOMs).");
 }
}
