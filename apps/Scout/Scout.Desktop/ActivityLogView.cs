using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Scout.Desktop;

public sealed class ActivityLogView : TextEditor
{
    // Saved events have a time prefix; live progress has the same message without it.
    // Anchor to outcome labels so filenames, dialogue and unresolved messages cannot
    // acquire a success highlight merely by containing the word "match".
    private static readonly Regex MatchLabel = new(@"^(?:\d{2}:\d{2}(?::\d{2})?\s+)?(?:Match found:|Saved match:|Matched:)\s", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Brush MatchBackground = Brush("#203C59");
    private static readonly Brush MatchForeground = Brush("#BBDDFF");

    public ActivityLogView()
    {
        IsReadOnly = true;
        WordWrap = true;
        Background = Brush("#202B3B");
        Foreground = Brush("#F0F4FA");
        BorderBrush = Brush("#38465A");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 9, 12, 9);
        Margin = new Thickness(0, 5, 0, 14);
        FontFamily = new FontFamily("Cascadia Mono");
        FontSize = 12;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        TextArea.SelectionBrush = Brush("#486A92");
        TextArea.TextView.BackgroundRenderers.Add(new MatchRows());
        TextArea.TextView.LineTransformers.Add(new MatchText());
    }

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed class MatchRows : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView view, DrawingContext drawingContext)
        {
            if (!view.VisualLinesValid) return;
            foreach (var line in view.VisualLines)
            {
                if (!MatchLabel.IsMatch(view.Document.GetText(line.FirstDocumentLine))) continue;
                // Include wrapped continuation lines and the unused width of the row.
                drawingContext.DrawRectangle(MatchBackground, null,
                    new Rect(0, line.VisualTop - view.VerticalOffset, view.ActualWidth, line.Height));
            }
        }
    }

    private sealed class MatchText : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (!MatchLabel.IsMatch(CurrentContext.Document.GetText(line))) return;
            ChangeLinePart(line.Offset, line.EndOffset, element => {
                element.TextRunProperties.SetForegroundBrush(MatchForeground);
                var font = element.TextRunProperties.Typeface;
                element.TextRunProperties.SetTypeface(new Typeface(font.FontFamily, font.Style, FontWeights.SemiBold, font.Stretch));
            });
        }
    }
}
