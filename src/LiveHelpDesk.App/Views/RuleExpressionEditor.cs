using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Core.Rules;

namespace LiveHelpDesk.App.Views;

/// <summary>
/// The editor of a rule's custom expression: colours fields, keywords and values, underlines the part in error and
/// suggests, as one types (or with Ctrl+Spazio), the fields, then the comparisons, then the values the field admits.
/// </summary>
public sealed class RuleExpressionEditor : UserControl
{
    public static readonly StyledProperty<TextDocument?> DocumentProperty =
        AvaloniaProperty.Register<RuleExpressionEditor, TextDocument?>(nameof(Document));

    public static readonly StyledProperty<IReadOnlyList<FieldInfo>?> FieldsProperty =
        AvaloniaProperty.Register<RuleExpressionEditor, IReadOnlyList<FieldInfo>?>(nameof(Fields));

    private readonly TextEditor _editor;
    private readonly Button _insertField;
    private readonly ExpressionColorizer _colorizer = new();
    private TextDocument? _shown;
    private CompletionWindow? _window;

    static RuleExpressionEditor()
    {
        DocumentProperty.Changed.AddClassHandler<RuleExpressionEditor>((control, _) => control.ShowDocument());
        FieldsProperty.Changed.AddClassHandler<RuleExpressionEditor>((control, _) => control.BuildFieldMenu());
    }

    public RuleExpressionEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = false,
            WordWrap = true,
            MinHeight = 64,
            MaxHeight = 220,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 13,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Document = new TextDocument(),
        };
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _insertField = new Button
        {
            Content = "Inserisci campo ▾",
            Margin = new Thickness(0, 0, 0, 4),
        };
        ToolTip.SetTip(_insertField, "Oppure iniziare a scrivere il nome del campo, o premere Ctrl+Spazio");

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4),
            Child = _editor,
        };

        Content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { _insertField, border },
        };

        BuildFieldMenu();
    }

    public TextDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public IReadOnlyList<FieldInfo>? Fields
    {
        get => GetValue(FieldsProperty);
        set => SetValue(FieldsProperty, value);
    }

    private IReadOnlyList<FieldInfo> KnownFields => Fields ?? [];

    // Not bound directly: a null document would leave AvaloniaEdit with nothing to edit.
    private void ShowDocument()
    {
        _window?.Close();
        if (_shown is not null)
        {
            _shown.TextChanged -= OnDocumentChanged;
        }

        _shown = Document;
        _editor.Document = _shown ?? new TextDocument();
        _editor.Document.TextChanged += OnDocumentChanged;
        OnDocumentChanged(null, EventArgs.Empty);
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        _colorizer.Update(_editor.Document.Text);
        _editor.TextArea.TextView.Redraw();
    }

    private void BuildFieldMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in KnownFields.GroupBy(f => string.IsNullOrEmpty(f.Group) ? "Altri" : f.Group))
        {
            var groupItem = new MenuItem { Header = group.Key };
            foreach (var field in group)
            {
                var item = new MenuItem { Header = $"{field.Label}   {field.Name}" };
                ToolTip.SetTip(item, field.Description);
                item.Click += (_, _) => InsertField(field);
                groupItem.Items.Add(item);
            }

            flyout.Items.Add(groupItem);
        }

        _insertField.Flyout = flyout;
    }

    private void InsertField(FieldInfo field)
    {
        var document = _editor.Document;
        var caret = _editor.CaretOffset;
        var before = caret > 0 && !char.IsWhiteSpace(document.GetCharAt(caret - 1)) && document.GetCharAt(caret - 1) != '(' ? " " : "";
        _editor.TextArea.Selection.ReplaceSelectionWithText($"{before}{field.Name} ");
        _editor.Focus();
        OpenLater();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            Open(explicitRequest: true);
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_window is not null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        var c = e.Text[^1];
        var context = RuleExpression.CompletionAt(_editor.Document.Text, _editor.CaretOffset);
        var typed = _editor.CaretOffset - context.ReplaceStart;
        var open = context.Kind switch
        {
            // the first letter of a field name, or of a word operator (contiene, tra...)
            ExprCompletionKind.Field or ExprCompletionKind.Operator when char.IsLetter(c) => typed == 1,
            ExprCompletionKind.Operator => c == ' ',
            ExprCompletionKind.Value => c is ' ' or '"' or '(' or ',' || (char.IsLetter(c) && typed == 1),
            _ => false,
        };

        if (open)
        {
            Open(explicitRequest: false);
        }
    }

    /// <summary>Opens the next suggestions once the text just completed is in the document.</summary>
    private void OpenLater() => Dispatcher.UIThread.Post(() => Open(explicitRequest: false), DispatcherPriority.Background);

    private void Open(bool explicitRequest)
    {
        if (_editor.Document is not { } document)
        {
            return;
        }

        var context = RuleExpression.CompletionAt(document.Text, _editor.CaretOffset);
        var field = context.Field is null ? null : KnownFields.FirstOrDefault(f => f.Key.Equals(context.Field, StringComparison.OrdinalIgnoreCase));
        var items = context.Kind switch
        {
            ExprCompletionKind.Field => FieldItems(),
            ExprCompletionKind.Operator => OperatorItems(field, context.Negated),
            ExprCompletionKind.Value => ValueItems(field),
            ExprCompletionKind.Logic when explicitRequest => LogicItems(),
            _ => [],
        };

        if (items.Count == 0)
        {
            return;
        }

        _window?.Close();
        var window = new CompletionWindow(_editor.TextArea) { StartOffset = context.ReplaceStart, MinWidth = 360 };
        foreach (var item in items)
        {
            window.CompletionList.CompletionData.Add(item);
        }

        window.Closed += (_, _) =>
        {
            if (_window == window)
            {
                _window = null;
            }
        };
        _window = window;
        window.Show();
        var typed = document.GetText(context.ReplaceStart, _editor.CaretOffset - context.ReplaceStart);
        if (typed.Length > 0)
        {
            window.CompletionList.SelectItem(typed);
        }
    }

    private List<ICompletionData> FieldItems() =>
    [
        .. KnownFields.Select(f => new Suggestion(
            f.Name + " ",
            $"{f.Label}   {f.Name}",
            string.IsNullOrEmpty(f.Description) ? f.Group : $"{f.Description}\n{f.Group}",
            OpenLater)),
    ];

    private List<ICompletionData> OperatorItems(FieldInfo? field, bool negated)
    {
        var withValues = field is { Choices.Count: > 0 } ? OpenLater : (Action?)null;
        Suggestion Op(string text, string label, string description, bool follow = true) =>
            new(text, $"{text.TrimEnd(' ', '(')}   {label}", description, follow ? withValues : null);

        var contains = Op("contiene ", "contiene il testo", "Es. titolo contiene \"fattura\" (maiuscole e minuscole non contano).");
        var startsWith = Op("iniziaCon ", "inizia con il testo", "Es. numero iniziaCon \"CAS-\".");
        var regex = Op("regex ", "corrisponde all'espressione regolare", "Es. oggetto regex \"^R[Ee]:\".", follow: false);
        var among = Op("tra (", "è uno tra", "Es. priorita tra (\"Alta\", \"Normale\"). I valori si separano con la virgola.");

        if (negated)
        {
            return [contains, startsWith, regex, among];
        }

        var equal = Op("= ", "è uguale a", "Es. priorita = \"Alta\".");
        var notEqual = Op("!= ", "è diverso da", "Es. coda != \"Urgenze\".");
        var notAmong = Op("non tra (", "non è nessuno tra", "Es. stato non tra (\"Chiuso\", \"Annullato\").");
        var empty = Op("= vuoto", "non è indicato", "Il campo è vuoto o non assegnato.", follow: false);
        var notEmpty = Op("!= vuoto", "è indicato", "Il campo ha un valore.", follow: false);
        var numbers = new[]
        {
            Op("> ", "è maggiore di", "Solo numeri, es. eta_minuti > 30.", follow: false),
            Op(">= ", "è almeno", "Solo numeri.", follow: false),
            Op("< ", "è minore di", "Solo numeri.", follow: false),
            Op("<= ", "è al massimo", "Solo numeri.", follow: false),
        };

        return field?.Kind switch
        {
            FieldKind.Number => [.. numbers, equal, notEqual],
            FieldKind.YesNo => [equal],
            FieldKind.Choice => [equal, notEqual, among, notAmong, .. field.IsNumeric ? numbers : []],
            FieldKind.Lookup => [equal, notEqual, among, notAmong, empty, notEmpty],
            _ => [contains, startsWith, equal, notEqual, among, notAmong, Op("non contiene ", "non contiene il testo", "Es. titolo non contiene \"test\"."), regex, empty, notEmpty],
        };
    }

    private static List<ICompletionData> ValueItems(FieldInfo? field)
    {
        if (field is null)
        {
            return [];
        }

        return
        [
            .. field.Choices.Where(c => !c.Hidden).Select(choice =>
            {
                var literal = Literal(field, choice);
                var shown = field.Kind != FieldKind.YesNo && choice.Value != "$me"
                    && !string.Equals(literal.Trim('"'), choice.Label, StringComparison.OrdinalIgnoreCase) ? $"   → {literal}" : "";
                return (ICompletionData)new Suggestion(literal, $"{choice.Label}{shown}", $"{field.Label}: {choice.Label}\nSi scrive {literal}", null);
            }),
        ];
    }

    /// <summary>How a value is written: by name when the field can be compared by name, else by code.</summary>
    internal static string Literal(FieldInfo field, Choice choice)
    {
        if (choice.Value == "$me")
        {
            return "$me";
        }

        if (field.Kind == FieldKind.YesNo)
        {
            return choice.Value == "true" ? "sì" : "no";
        }

        if (field.HasNames)
        {
            return RuleExpression.Quote(choice.Label);
        }

        return field.IsNumeric && double.TryParse(choice.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? choice.Value
            : RuleExpression.Quote(choice.Value);
    }

    private List<ICompletionData> LogicItems() =>
    [
        new Suggestion("e ", "e   devono valere entrambe", "Es. priorita = \"Alta\" e coda = \"Urgenze\".", OpenLater),
        new Suggestion("o ", "o   basta che ne valga una", "Es. coda = \"Urgenze\" o priorita = \"Alta\". Usare le parentesi per raggruppare.", OpenLater),
        new Suggestion("e non ", "e non   la seconda non deve valere", "Es. coda = \"Urgenze\" e non in_sospeso.", OpenLater),
    ];

    private sealed class Suggestion(string text, string display, string description, Action? then) : ICompletionData
    {
        public IImage? Image => null;

        public string Text { get; } = text;

        public object Content { get; } = new TextBlock { Text = display };

        public object Description { get; } = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var document = textArea.Document;
            var insert = Text;
            var end = completionSegment.EndOffset;

            // No double space when the text after the caret already starts with one.
            if (insert.EndsWith(' ') && end < document.TextLength && char.IsWhiteSpace(document.GetCharAt(end)))
            {
                insert = insert.TrimEnd();
            }

            // Completing a value replaces the closing quote already there, instead of leaving a stray one.
            var length = completionSegment.Length;
            if (insert.StartsWith('"') && completionSegment.Length > 0 && document.GetCharAt(completionSegment.Offset) == '"'
                && end < document.TextLength && document.GetCharAt(end) == '"')
            {
                length++;
            }

            document.Replace(completionSegment.Offset, length, insert);
            then?.Invoke();
        }
    }

    /// <summary>Colours the expression by token, and underlines the part in error.</summary>
    private sealed class ExpressionColorizer : DocumentColorizingTransformer
    {
        private static readonly IBrush FieldBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        private static readonly IBrush KeywordBrush = new SolidColorBrush(Color.FromRgb(0x93, 0x33, 0xEA));
        private static readonly IBrush ValueBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x94, 0x88));
        private static readonly IBrush StringBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
        private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        private static readonly IBrush ErrorBackground = new SolidColorBrush(Color.FromArgb(0x30, 0xDC, 0x26, 0x26));

        private IReadOnlyList<ExprToken> _tokens = [];
        private RuleExpressionError? _error;

        public void Update(string text)
        {
            var parsed = RuleExpression.Parse(text);
            _tokens = parsed.Tokens;
            _error = string.IsNullOrWhiteSpace(text) ? null : parsed.Error;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var lineEnd = line.Offset + line.Length;
            foreach (var token in _tokens)
            {
                var start = Math.Max(token.Start, line.Offset);
                var end = Math.Min(token.End, lineEnd);
                if (start >= end)
                {
                    continue;
                }

                var (brush, bold) = token.Kind switch
                {
                    ExprTokenKind.Word => token.Role switch
                    {
                        ExprWordRole.None => (RuleFields.Resolve(token.Text) is null ? ErrorBrush : FieldBrush, true),
                        ExprWordRole.Literal => (ValueBrush, false),
                        _ => (KeywordBrush, true),
                    },
                    ExprTokenKind.String => (StringBrush, false),
                    ExprTokenKind.Number or ExprTokenKind.Me => (ValueBrush, false),
                    ExprTokenKind.Invalid => (ErrorBrush, true),
                    _ => ((IBrush?)null, false),
                };

                if (brush is null)
                {
                    continue;
                }

                ChangeLinePart(start, end, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    if (bold)
                    {
                        element.TextRunProperties.SetTypeface(new Typeface(
                            element.TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
                    }
                });
            }

            if (_error is { Length: > 0 } error)
            {
                var start = Math.Max(error.Start, line.Offset);
                var end = Math.Min(error.Start + error.Length, lineEnd);
                if (start < end)
                {
                    ChangeLinePart(start, end, element =>
                    {
                        element.TextRunProperties.SetBackgroundBrush(ErrorBackground);
                        element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    });
                }
            }
        }
    }
}
