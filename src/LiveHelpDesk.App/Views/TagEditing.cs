using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using LiveHelpDesk.Core.Notifications;

namespace LiveHelpDesk.App.Views;

/// <summary>
/// Makes a text editor aware of the <c>{{campo}}</c> tags: typing <c>{{</c> or Ctrl+Spazio offers the fields with
/// their description, known tags are coloured and unknown ones shown in red.
/// </summary>
internal static class TagEditing
{
    public static void Attach(TextEditor editor, bool singleLine)
    {
        CompletionWindow? window = null;

        editor.TextArea.TextView.LineTransformers.Add(new TagColorizer());
        editor.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text == "{" && editor.CaretOffset >= 2 && editor.Document.GetCharAt(editor.CaretOffset - 2) == '{')
            {
                Open(editor.CaretOffset - 2);
            }
        };

        editor.TextArea.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    Open(StartOfTag(editor.Document, editor.CaretOffset));
                }
                else if (singleLine && e.Key == Key.Enter && window is null)
                {
                    e.Handled = true;
                }
            },
            RoutingStrategies.Tunnel);

        if (singleLine)
        {
            // A pasted line break would make a title the service refuses.
            editor.TextArea.TextEntering += (_, e) =>
            {
                if (e.Text is { } text && (text.Contains('\n') || text.Contains('\r')))
                {
                    e.Handled = true;
                    editor.TextArea.Selection.ReplaceSelectionWithText(text.ReplaceLineEndings(" "));
                }
            };
        }

        void Open(int start)
        {
            if (editor.Document is null)
            {
                return;
            }

            window?.Close();
            window = new CompletionWindow(editor.TextArea) { StartOffset = start, MinWidth = 320 };
            foreach (var field in TemplateFields.All)
            {
                window.CompletionList.CompletionData.Add(new TagCompletionData(field));
            }

            window.Closed += (_, _) => window = null;
            window.Show();
            var typed = editor.Document.GetText(start, editor.CaretOffset - start);
            if (typed.Length > 0)
            {
                window.CompletionList.SelectItem(typed);
            }
        }
    }

    /// <summary>Inserts <c>{{campo}}</c> at the caret (from the "Inserisci campo" menu).</summary>
    public static void Insert(TextEditor editor, string key)
    {
        if (editor.Document is null)
        {
            return;
        }

        editor.TextArea.Selection.ReplaceSelectionWithText($"{{{{{key}}}}}");
        editor.Focus();
    }

    /// <summary>Wraps the selection in a Markdown marker (<c>**</c>, <c>_</c>, <c>~~</c>), or inserts an empty pair.</summary>
    public static void Wrap(TextEditor editor, string before, string after)
    {
        if (editor.Document is null)
        {
            return;
        }

        var start = editor.SelectionStart;
        var selected = editor.SelectedText;
        editor.Document.Replace(start, editor.SelectionLength, before + selected + after);
        if (selected.Length == 0)
        {
            editor.CaretOffset = start + before.Length;
        }
        else
        {
            editor.Select(start + before.Length, selected.Length);
        }

        editor.Focus();
    }

    /// <summary>Starts every selected line (or the caret's line) with <paramref name="prefix"/>.</summary>
    public static void PrefixLines(TextEditor editor, string prefix)
    {
        if (editor.Document is not { } document)
        {
            return;
        }

        var first = document.GetLineByOffset(editor.SelectionStart);
        var last = document.GetLineByOffset(editor.SelectionStart + editor.SelectionLength);
        document.BeginUpdate();
        try
        {
            for (var line = first; line is not null; line = line.NextLine)
            {
                if (!document.GetText(line.Offset, line.Length).StartsWith(prefix, StringComparison.Ordinal))
                {
                    document.Insert(line.Offset, prefix);
                }

                if (line == last)
                {
                    break;
                }
            }
        }
        finally
        {
            document.EndUpdate();
        }

        editor.Focus();
    }

    /// <summary>Where the tag being typed starts: an unclosed <c>{{</c> on the caret's line, or the word before the caret.</summary>
    private static int StartOfTag(TextDocument document, int caret)
    {
        var line = document.GetLineByOffset(caret);
        var before = document.GetText(line.Offset, caret - line.Offset);
        var open = before.LastIndexOf("{{", StringComparison.Ordinal);
        if (open >= 0 && before.IndexOf("}}", open, StringComparison.Ordinal) < 0)
        {
            return line.Offset + open;
        }

        var start = caret;
        while (start > line.Offset && (char.IsLetterOrDigit(document.GetCharAt(start - 1)) || document.GetCharAt(start - 1) == '_'))
        {
            start--;
        }

        return start;
    }

    private sealed class TagCompletionData(TemplateField field) : ICompletionData
    {
        public IImage? Image => null;

        public string Text { get; } = $"{{{{{field.Key}}}}}";

        public object Content { get; } = new TextBlock { Text = $"{field.Label}   {{{{{field.Key}}}}}" };

        public object Description { get; } = new TextBlock { Text = $"{field.Description}\nEsempio: {field.Sample}", TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var document = textArea.Document;
            var length = completionSegment.Length;
            var end = completionSegment.EndOffset;

            // Completing inside an existing tag replaces it whole instead of leaving stray braces.
            while (end < document.TextLength && (char.IsLetterOrDigit(document.GetCharAt(end)) || document.GetCharAt(end) == '_'))
            {
                end++;
                length++;
            }

            if (end + 1 < document.TextLength && document.GetText(end, 2) == "}}")
            {
                length += 2;
            }

            document.Replace(completionSegment.Offset, length, Text);
        }
    }

    private sealed class TagColorizer : DocumentColorizingTransformer
    {
        private static readonly IBrush Known = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        private static readonly IBrush Unknown = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line.Offset, line.Length);
            foreach (System.Text.RegularExpressions.Match match in TemplateRenderer.TagPattern().Matches(text))
            {
                var known = TemplateFields.IsKnown(match.Groups[1].Value.Trim());
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(known ? Known : Unknown);
                    element.TextRunProperties.SetTypeface(new Typeface(
                        element.TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
                });
            }
        }
    }
}
