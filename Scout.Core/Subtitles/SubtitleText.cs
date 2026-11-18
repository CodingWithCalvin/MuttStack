using System.Text;
namespace Scout.Core.Subtitles;

static class SubtitleText {
 public static (string Text,string Encoding) Decode(byte[] bytes) {
  // Subtitle archives commonly contain older Windows text. Honor Unicode BOMs;
  // only fall back to Windows-1252 when BOM-less UTF-8 is invalid.
  if(bytes.AsSpan().StartsWith(new byte[]{0xff,0xfe,0,0}))return (new UTF32Encoding(false,false,true).GetString(bytes,4,bytes.Length-4),"utf-32le");
  if(bytes.AsSpan().StartsWith(new byte[]{0,0,0xfe,0xff}))return (new UTF32Encoding(true,false,true).GetString(bytes,4,bytes.Length-4),"utf-32be");
  if(bytes.AsSpan().StartsWith(new byte[]{0xff,0xfe}))return (new UnicodeEncoding(false,false,true).GetString(bytes,2,bytes.Length-2),"utf-16le");
  if(bytes.AsSpan().StartsWith(new byte[]{0xfe,0xff}))return (new UnicodeEncoding(true,false,true).GetString(bytes,2,bytes.Length-2),"utf-16be");
  if(bytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf}))return (new UTF8Encoding(false,true).GetString(bytes,3,bytes.Length-3),"utf-8-bom");
  try{return (new UTF8Encoding(false,true).GetString(bytes),"utf-8");}
  catch(DecoderFallbackException) {
   Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
   return (Encoding.GetEncoding(1252,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes),"windows-1252");
  }
 }
}
