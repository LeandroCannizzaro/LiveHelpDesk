using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Core.Rules;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>A checkbox in a list (an event, a channel).</summary>
public sealed partial class SelectableItem(Choice choice, bool selected = false) : ObservableObject
{
    public string Value { get; } = choice.Value;

    public string Label { get; } = choice.Label;

    [ObservableProperty]
    private bool _isSelected = selected;
}

/// <summary>One line of a rule's condition: "Priorità · è · Alta".</summary>
public sealed partial class ConditionRow : ObservableObject
{
    private readonly IReadOnlyList<FieldInfo> _fields;
    private readonly Action<ConditionRow> _remove;

    public ConditionRow(IReadOnlyList<FieldInfo> fields, Action<ConditionRow> remove, FieldInfo? field = null)
    {
        _fields = fields;
        _remove = remove;
        Fields = fields;
        _field = field ?? fields[0];
        Rebuild();
    }

    public IReadOnlyList<FieldInfo> Fields { get; }

    [ObservableProperty]
    private FieldInfo _field;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowValue), nameof(ShowChoices), nameof(ShowText))]
    private Choice? _operator;

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private Choice? _valueChoice;

    public IReadOnlyList<Choice> Operators { get; private set; } = [];

    public IReadOnlyList<Choice> Choices => Field.Choices;

    /// <summary>An operator that compares nothing (is empty / is set) hides the value.</summary>
    public bool ShowValue => Operator is null || RuleCatalog.NeedsValue(Operator.Value);

    /// <summary>One value out of a fixed list: a drop-down. "One of" takes several, so it stays a text box.</summary>
    public bool ShowChoices => ShowValue && Field.Choices.Count > 0 && Operator?.Value is "eq" or "ne" && Field.Kind != FieldKind.Number;

    public bool ShowText => ShowValue && !ShowChoices;

    partial void OnFieldChanged(FieldInfo value)
    {
        Rebuild();
        Value = "";
        ValueChoice = value.Choices.FirstOrDefault();
        OnPropertyChanged(nameof(Operators));
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(ShowChoices));
        OnPropertyChanged(nameof(ShowText));
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    private void Rebuild()
    {
        Operators = RuleCatalog.OperatorsFor(Field.Kind);
        Operator = Operators[0];
        ValueChoice = Field.Choices.FirstOrDefault();
    }

    /// <summary>The line as a rule condition. Values that are numbers in the file are written as numbers.</summary>
    public RuleCondition ToCondition()
    {
        var op = Operator?.Value ?? "eq";
        var condition = new RuleCondition { Field = Field.Key, Op = op };
        if (!RuleCatalog.NeedsValue(op))
        {
            return condition;
        }

        var text = (ShowChoices ? ValueChoice?.Value : Value) ?? "";
        condition.Value = op is "in" or "notIn"
            ? new JsonArray([.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => Scalar(item))])
            : Scalar(text);
        return condition;
    }

    private JsonNode? Scalar(string text)
    {
        if (Field.Kind == FieldKind.YesNo)
        {
            return JsonValue.Create(text.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        if (Field.IsNumeric && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number % 1 == 0 ? JsonValue.Create((long)number) : JsonValue.Create(number);
        }

        return JsonValue.Create(text);
    }

    /// <summary>Reads a rule condition back into a line; the operator and value are chosen to match.</summary>
    public static ConditionRow From(RuleCondition condition, IReadOnlyList<FieldInfo> fields, Action<ConditionRow> remove)
    {
        var field = fields.FirstOrDefault(f => f.Key.Equals(condition.Field, StringComparison.OrdinalIgnoreCase))
            ?? RuleCatalog.Custom(condition.Field ?? "", IsNumber(condition.Value));
        var all = fields.Contains(field) ? fields : [.. fields, field];
        var row = new ConditionRow(all, remove, field);
        var known = row.Operators.FirstOrDefault(o => o.Value.Equals(condition.Op, StringComparison.OrdinalIgnoreCase));
        if (known is null && !string.IsNullOrEmpty(condition.Op))
        {
            // An operator the form has no phrase for (typed in the JSON): kept as it is, shown by its name.
            known = new Choice(condition.Op, condition.Op);
            row.Operators = [.. row.Operators, known];
        }

        row.Operator = known ?? row.Operators[0];

        var text = condition.Value switch
        {
            JsonArray array => string.Join(", ", array.Select(Plain)),
            { } value => Plain(value),
            _ => "",
        };

        if (row.Operator.Value is "eq" or "ne" && field.Choices.Count > 0)
        {
            row.ValueChoice = field.Choices.FirstOrDefault(c => c.Value.Equals(text, StringComparison.OrdinalIgnoreCase))
                ?? new Choice(text, text); // an id the lookup list does not know: still shown, still kept
            if (!field.Choices.Contains(row.ValueChoice))
            {
                row.ExtraChoice(row.ValueChoice);
            }
        }
        else
        {
            row.Value = text;
        }

        row.OnPropertyChanged(nameof(ShowValue));
        row.OnPropertyChanged(nameof(ShowChoices));
        row.OnPropertyChanged(nameof(ShowText));
        return row;
    }

    private static bool IsNumber(JsonNode? node) =>
        (node is JsonArray array ? array.FirstOrDefault() : node) is JsonValue value
        && value.GetValueKind() == System.Text.Json.JsonValueKind.Number;

    private void ExtraChoice(Choice choice)
    {
        // A value outside the offered list is added so the drop-down can show it.
        Field = Field with { Choices = [.. Field.Choices, choice] };
        ValueChoice = choice;
    }

    private static string Plain(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "";
}

/// <summary>One rule, in editable form: "must hold" rows plus a free expression. Conditions an expression cannot say stay as they are and are edited as JSON.</summary>
public sealed partial class RuleViewModel : ObservableObject
{
    private readonly Rule _original;

    public RuleViewModel(Rule rule, IReadOnlyList<FieldInfo> fields)
    {
        _original = rule;
        Fields = fields;
        Id = rule.Id;
        _name = rule.Name;
        _enabled = rule.Enabled;
        _severity = RuleCatalog.Severities.First(c => c.Value == rule.Actions.Severity.ToString());
        _detail = RuleCatalog.Details.First(c => c.Value == rule.Actions.Detail.ToString());
        _escalateEnabled = rule.Actions.EscalateAfterMinutes is > 0;
        _escalateMinutes = (rule.Actions.EscalateAfterMinutes ?? 15).ToString(CultureInfo.InvariantCulture);

        bool HasEvent(string value) => rule.Events.Any(t => t.ToString() == value);
        bool HasChannel(string value) => rule.Actions.Channels.Contains(value, StringComparer.OrdinalIgnoreCase);
        bool HasEscalation(string value) => rule.Actions.EscalateChannels.Contains(value, StringComparer.OrdinalIgnoreCase);
        EventChoices = [.. RuleCatalog.Offered(RuleCatalog.Events, HasEvent).Select(e => new SelectableItem(e, HasEvent(e.Value)))];
        ChannelChoices = [.. RuleCatalog.Offered(RuleCatalog.Channels, HasChannel).Select(c => new SelectableItem(c, HasChannel(c.Value)))];
        EscalateChannelChoices = [.. RuleCatalog.Offered(RuleCatalog.Channels, HasEscalation).Select(c => new SelectableItem(c, HasEscalation(c.Value)))];

        IsAdvanced = !TryReadCondition(rule.When, out var expression);
        ExpressionDocument = new TextDocument(expression);
        CheckExpression();
        ExpressionDocument.TextChanged += (_, _) =>
        {
            CheckExpression();
            Raise();
        };

        foreach (var item in EventChoices.Concat(ChannelChoices).Concat(EscalateChannelChoices))
        {
            item.PropertyChanged += (_, _) => Raise();
        }

        Watch(AllRows);
    }

    /// <summary>Raised whenever anything about the rule is changed by the user.</summary>
    public event EventHandler? Edited;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Raise();
    }

    private void Raise() => Edited?.Invoke(this, EventArgs.Empty);

    private void Watch(ObservableCollection<ConditionRow> rows)
    {
        foreach (var row in rows)
        {
            row.PropertyChanged += (_, _) => Raise();
        }

        rows.CollectionChanged += (_, e) =>
        {
            foreach (ConditionRow row in e.NewItems ?? Array.Empty<ConditionRow>())
            {
                row.PropertyChanged += (_, _) => Raise();
            }

            Raise();
        };
    }

    public string Id { get; private set; }

    public IReadOnlyList<FieldInfo> Fields { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _name;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private Choice _severity;

    [ObservableProperty]
    private Choice _detail;

    [ObservableProperty]
    private bool _escalateEnabled;

    [ObservableProperty]
    private string _escalateMinutes;

    public ObservableCollection<SelectableItem> EventChoices { get; }

    public ObservableCollection<SelectableItem> ChannelChoices { get; }

    public ObservableCollection<SelectableItem> EscalateChannelChoices { get; }

    /// <summary>Conditions that must all hold.</summary>
    public ObservableCollection<ConditionRow> AllRows { get; } = [];

    /// <summary>The custom expression, which must hold too (as if in brackets, in "e" with the rows).</summary>
    public TextDocument ExpressionDocument { get; }

    /// <summary>What is wrong with the expression as typed, or null when it is empty or valid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExpressionError))]
    private string? _expressionError;

    [ObservableProperty]
    private bool _isExpressionValid;

    public bool HasExpressionError => ExpressionError is not null;

    /// <summary>True when the condition is too elaborate for the form and is kept untouched.</summary>
    public bool IsAdvanced { get; }

    public IReadOnlyList<Choice> Severities => RuleCatalog.Severities;

    public IReadOnlyList<Choice> Details => RuleCatalog.Details;

    public string Summary =>
        string.IsNullOrWhiteSpace(Name) ? "(senza nome)" : Name;

    [RelayCommand]
    private void AddAll() => AllRows.Add(new ConditionRow(Fields, row => AllRows.Remove(row)));

    private void CheckExpression()
    {
        var text = ExpressionDocument.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ExpressionError = null;
            IsExpressionValid = false;
            return;
        }

        var error = RuleExpression.Parse(text).Error;
        ExpressionError = error is null ? null : $"Errore, {error.Describe(text)}";
        IsExpressionValid = error is null;
    }

    /// <summary>The rule as the file wants it.</summary>
    public Rule ToRule()
    {
        var events = EventChoices.Where(e => e.IsSelected).Select(e => Enum.Parse<TicketEventType>(e.Value)).ToList();
        var channels = ChannelChoices.Where(c => c.IsSelected).Select(c => c.Value).ToList();
        var escalationChannels = EscalateChannelChoices.Where(c => c.IsSelected).Select(c => c.Value).ToList();
        var minutes = int.TryParse(EscalateMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;

        return new Rule
        {
            Id = Id,
            Name = Name.Trim(),
            Enabled = Enabled,
            Events = events,
            When = IsAdvanced ? _original.When : BuildCondition(),
            Actions = new RuleActions
            {
                Channels = channels,
                Severity = Enum.Parse<Severity>(Severity.Value),
                Detail = Enum.Parse<DetailLevel>(Detail.Value),
                EscalateAfterMinutes = EscalateEnabled ? minutes : null,
                EscalateChannels = EscalateEnabled ? escalationChannels : [],
            },
        };
    }

    /// <summary>Gives the rule an id from its name when it has none (new rules); ids never change afterwards.</summary>
    public void EnsureId(IEnumerable<string> taken)
    {
        if (!string.IsNullOrWhiteSpace(Id))
        {
            return;
        }

        var slug = new string([.. Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        slug = string.IsNullOrEmpty(slug) ? "regola" : slug;
        var candidate = slug;
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; used.Contains(candidate); i++)
        {
            candidate = $"{slug}-{i}";
        }

        Id = candidate;
        OnPropertyChanged(nameof(Id));
    }

    /// <summary>The rows and the expression, all in "e": a single part stands alone, several go in an "all" group.</summary>
    private RuleCondition? BuildCondition()
    {
        var all = AllRows.Select(r => r.ToCondition()).ToList();
        var expression = ExpressionDocument.Text.Trim();
        if (expression.Length > 0)
        {
            all.Add(new RuleCondition { Expression = expression });
        }

        return all.Count switch
        {
            0 => null,
            1 => all[0],
            _ => new RuleCondition { All = all },
        };
    }

    /// <summary>
    /// Fills the rows from the rule's condition. What is not a plain "must hold" line (alternatives, exclusions, groups)
    /// is written into the expression; false when the condition has something an expression cannot say.
    /// </summary>
    private bool TryReadCondition(RuleCondition? when, out string expression)
    {
        expression = "";
        if (when is null)
        {
            return true;
        }

        static bool IsLeaf(RuleCondition c) =>
            c.Field is not null && c.All is null && c.Any is null && c.Not is null && c.Expression is null;

        var leaves = new List<RuleCondition>();
        var parts = new List<string>();

        if (IsLeaf(when))
        {
            leaves.Add(when);
        }
        else if (when.Field is not null)
        {
            if (RuleExpression.Format(when) is not { } whole)
            {
                return false;
            }

            parts.Add(whole);
        }
        else
        {
            foreach (var child in when.All ?? [])
            {
                if (IsLeaf(child))
                {
                    leaves.Add(child);
                }
                else if (child is { Field: null, All: null, Any: null, Not: null, Expression: { } text })
                {
                    parts.Add(text); // kept as typed, mistakes included, so they can be mended in the editor
                }
                else if (RuleExpression.Format(child) is { } formatted)
                {
                    parts.Add(formatted);
                }
                else
                {
                    return false;
                }
            }

            foreach (var group in new[] { when.Any is null ? null : new RuleCondition { Any = when.Any }, when.Not is null ? null : new RuleCondition { Not = when.Not } })
            {
                if (group is null)
                {
                    continue;
                }

                if (RuleExpression.Format(group) is not { } formatted)
                {
                    return false;
                }

                parts.Add(formatted);
            }

            if (when.Expression is { } own)
            {
                parts.Add(own);
            }
        }

        foreach (var leaf in leaves)
        {
            AllRows.Add(ConditionRow.From(leaf, Fields, row => AllRows.Remove(row)));
        }

        expression = parts.Count == 1 ? parts[0] : RuleExpression.Join(parts);
        return true;
    }
}