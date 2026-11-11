using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LiveHelpDesk.Core.Notifications;

public enum TemplateFormat
{
    /// <summary>Text without formatting: toast, overlay, SMS, WhatsApp.</summary>
    Plain,

    /// <summary>The Markdown subset Teams cards understand.</summary>
    Markdown,

    /// <summary>HTML, for e-mail.</summary>
    Html,
}

/// <summary>
/// Fills the <c>{{campo}}</c> tags of a notification text and turns the Markdown into what each channel can show.
/// Values are escaped before parsing, so a ticket title with asterisks never turns into bold text.
/// </summary>
public static partial class TemplateRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    [GeneratedRegex(@"\{\{(.*?)\}\}")]
    public static partial Regex TagPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    /// <summary>E-mail gets HTML, Teams gets Markdown, everything else plain text.</summary>
    public static TemplateFormat FormatFor(string channel) => channel.ToLowerInvariant() switch
    {
        "email" => TemplateFormat.Html,
        "teams" => TemplateFormat.Markdown,
        _ => TemplateFormat.Plain,
    };

    /// <summary>Problems with the tags of a text: unknown fields and braces left open.</summary>
    public static IReadOnlyList<string> CheckTags(string text)
    {
        var problems = new List<string>();
        foreach (Match match in TagPattern().Matches(text))
        {
            var key = match.Groups[1].Value.Trim();
            if (!Identifier().IsMatch(key) || !TemplateFields.IsKnown(key))
            {
                problems.Add($"campo sconosciuto «{match.Value}».");
            }
        }

        var rest = TagPattern().Replace(text, "");
        if (rest.Contains("{{") || rest.Contains("}}"))
        {
            problems.Add("parentesi graffe non chiuse: i campi si scrivono {{campo}}.");
        }

        return problems;
    }

    /// <summary>The title is one line of plain text: tags are filled in as they are.</summary>
    public static string RenderTitle(string template, IReadOnlyDictionary<string, string?> values) =>
        string.Join(' ', Expand(template, values, markdown: false).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string RenderBody(string template, IReadOnlyDictionary<string, string?> values, TemplateFormat format)
    {
        var markdown = ExpandMarkdown(template, values);
        return format == TemplateFormat.Html
            ? Markdig.Markdown.ToHtml(markdown, Pipeline).Trim()
            : Blocks(Markdig.Markdown.Parse(markdown, Pipeline), format == TemplateFormat.Markdown).Trim();
    }

    /// <summary>The text with its tags filled in, still Markdown (values escaped).</summary>
    public static string ExpandMarkdown(string template, IReadOnlyDictionary<string, string?> values) => Expand(template, values, markdown: true);

    /// <summary>Markdown parsed with the same rules used for sending, for the editor's preview.</summary>
    public static MarkdownDocument Parse(string markdown) => Markdig.Markdown.Parse(markdown, Pipeline);

    private static string Expand(string template, IReadOnlyDictionary<string, string?> values, bool markdown) =>
        TagPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            if (!values.TryGetValue(key, out var value))
            {
                return markdown ? Escape(match.Value) : match.Value;
            }

            if (string.IsNullOrEmpty(value) || !markdown)
            {
                return value ?? "";
            }

            // An angle-bracket address works both alone (autolink) and inside [text](...).
            return key.Equals(TemplateFields.Link, StringComparison.OrdinalIgnoreCase) ? $"<{value}>" : Escape(value);
        });

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c > 127 || char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('\\').Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Blocks(ContainerBlock container, bool markdown) =>
        string.Join(markdown ? "\n\n" : "\n", container.Select(b => Block(b, markdown)).Where(s => s.Length > 0));

    // Text that reached the output as plain words (a ticket title with an asterisk) must not turn into formatting in Teams.
    private static string EscapeFormatting(string text)
    {
        if (text.AsSpan().IndexOfAny("\\*_~[]`") < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '[' or ']' or '`')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Block(Block block, bool markdown) => block switch
    {
        HeadingBlock heading => markdown ? $"**{Inlines(heading.Inline, markdown)}**" : Inlines(heading.Inline, markdown),
        ListBlock list => List(list, markdown),
        QuoteBlock quote => markdown ? "> " + Blocks(quote, markdown).Replace("\n", "\n> ") : Blocks(quote, markdown),
        CodeBlock code => code.Lines.ToString(),
        ThematicBreakBlock => markdown ? "---" : "———",
        ContainerBlock container => Blocks(container, markdown),
        LeafBlock leaf => Inlines(leaf.Inline, markdown),
        _ => "",
    };

    private static string List(ListBlock list, bool markdown)
    {
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        var items = new List<string>();
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var bullet = list.IsOrdered ? $"{number++}. " : markdown ? "- " : "• ";
            var content = string.Join("\n", item.Select(b => Block(b, markdown)).Where(s => s.Length > 0));
            items.Add(bullet + content.Replace("\n", "\n" + new string(' ', bullet.Length)));
        }

        return string.Join("\n", items);
    }

    private static string Inlines(ContainerInline? container, bool markdown)
    {
        if (container is null)
        {
            return "";
        }

        var builder = new StringBuilder();
        foreach (var inline in container)
        {
            builder.Append(Inline(inline, markdown));
        }

        return builder.ToString();
    }

    private static string Inline(Inline inline, bool markdown)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return markdown ? EscapeFormatting(literal.Content.ToString()) : literal.Content.ToString();
            case LineBreakInline:
                return "\n";
            case CodeInline code:
                return code.Content;
            case HtmlEntityInline entity:
                return entity.Transcoded.ToString();
            case AutolinkInline autolink:
                return autolink.Url;
            case LinkInline link:
                var text = Inlines(link, markdown);
                var url = link.Url;
                if (link.IsImage || string.IsNullOrWhiteSpace(url))
                {
                    return text;
                }

                if (markdown)
                {
                    return link.IsAutoLink ? url : $"[{(text.Length == 0 ? url : text)}]({url})";
                }

                return link.IsAutoLink || text.Length == 0 || text == url ? url : $"{text} ({url})";
            case EmphasisInline emphasis:
                var inner = Inlines(emphasis, markdown);
                if (!markdown || emphasis.DelimiterChar == '~' || inner.Length == 0)
                {
                    return inner;
                }

                return emphasis.DelimiterCount >= 2 ? $"**{inner}**" : $"_{inner}_";
            case ContainerInline container:
                return Inlines(container, markdown);
            default:
                return "";
        }
    }
}
