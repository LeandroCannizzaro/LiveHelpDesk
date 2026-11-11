using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LiveHelpDesk.Core.Notifications;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LiveHelpDesk.App.Views;

/// <summary>Shows Markdown roughly as an e-mail or a Teams card would: bold, italics, lists and links.</summary>
public sealed class MarkdownPreview : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownPreview, string?>(nameof(Markdown));

    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));

    static MarkdownPreview()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownPreview>((control, _) => control.Rebuild());
    }

    public MarkdownPreview()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        if (!string.IsNullOrEmpty(Markdown))
        {
            AddBlocks(inlines, TemplateRenderer.Parse(Markdown), "");
        }

        while (inlines.Count > 0 && inlines[^1] is LineBreak)
        {
            inlines.RemoveAt(inlines.Count - 1);
        }

        Inlines = inlines;
    }

    private static void AddBlocks(InlineCollection target, ContainerBlock container, string indent)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var bold = new Bold { FontSize = 16 };
                    AddInlines(bold.Inlines, heading.Inline);
                    target.Add(bold);
                    target.Add(new LineBreak());
                    target.Add(new LineBreak());
                    break;
                case ListBlock list:
                    var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        target.Add(new Run(indent + (list.IsOrdered ? $"{number++}. " : "•  ")));
                        AddBlocks(target, item, indent + "    ");
                    }

                    if (indent.Length == 0)
                    {
                        target.Add(new LineBreak());
                    }

                    break;
                case QuoteBlock quote:
                    var italic = new Italic { Foreground = Brushes.Gray };
                    AddBlocks(italic.Inlines, quote, indent);
                    target.Add(italic);
                    break;
                case CodeBlock code:
                    target.Add(new Run(code.Lines.ToString()) { FontFamily = new FontFamily("Consolas") });
                    target.Add(new LineBreak());
                    target.Add(new LineBreak());
                    break;
                case ThematicBreakBlock:
                    target.Add(new Run("────────────") { Foreground = Brushes.Gray });
                    target.Add(new LineBreak());
                    break;
                case ContainerBlock inner:
                    AddBlocks(target, inner, indent);
                    break;
                case LeafBlock leaf:
                    AddInlines(target, leaf.Inline);
                    target.Add(new LineBreak());
                    if (container is not ListItemBlock)
                    {
                        target.Add(new LineBreak());
                    }

                    break;
            }
        }
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas") });
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case AutolinkInline autolink:
                    target.Add(new Underline { Foreground = LinkBrush, Inlines = { new Run(autolink.Url) } });
                    break;
                case LinkInline link:
                    var span = new Underline { Foreground = LinkBrush };
                    AddInlines(span.Inlines, link);
                    if (span.Inlines.Count == 0)
                    {
                        span.Inlines.Add(new Run(link.Url ?? ""));
                    }

                    target.Add(span);
                    break;
                case EmphasisInline emphasis:
                    Span styled = emphasis.DelimiterChar == '~'
                        ? new Span { TextDecorations = Avalonia.Media.TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(styled.Inlines, emphasis);
                    target.Add(styled);
                    break;
                case ContainerInline inner:
                    AddInlines(target, inner);
                    break;
            }
        }
    }
}
