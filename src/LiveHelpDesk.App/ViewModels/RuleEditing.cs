using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
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
        Field = new FieldInfo(Field.Key, Field.Label, Field.Kind, [.. Field.Choices, choice]);
        ValueChoice = choice;
    }

    private static string Plain(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "";
}

/// <summary>One rule, in editable form. Conditions the editor cannot draw (deep nesting) stay as they are and are edited as JSON.</summary>
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

        IsAdvanced = !TryReadCondition(rule.When);

        foreach (var item in EventChoices.Concat(ChannelChoices).Concat(EscalateChannelChoices))
        {
            item.PropertyChanged += (_, _) => Raise();
        }

        Watch(AllRows);
        Watch(AnyRows);
        Watch(NotRows);
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

    /// <summary>At least one of these must hold.</summary>
    public ObservableCollection<ConditionRow> AnyRows { get; } = [];

    /// <summary>None of these may hold.</summary>
    public ObservableCollection<ConditionRow> NotRows { get; } = [];

    /// <summary>True when the condition is too elaborate for the form and is kept untouched.</summary>
    public bool IsAdvanced { get; }

    public IReadOnlyList<Choice> Severities => RuleCatalog.Severities;

    public IReadOnlyList<Choice> Details => RuleCatalog.Details;

    public string Summary =>
        string.IsNullOrWhiteSpace(Name) ? "(senza nome)" : Name;

    [RelayCommand]
    private void AddAll() => AllRows.Add(NewRow(AllRows));

    [RelayCommand]
    private void AddAny() => AnyRows.Add(NewRow(AnyRows));

    [RelayCommand]
    private void AddNot() => NotRows.Add(NewRow(NotRows));

    private ConditionRow NewRow(ObservableCollection<ConditionRow> into) =>
        new(Fields, row => into.Remove(row));

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

    private RuleCondition? BuildCondition()
    {
        var all = AllRows.Select(r => r.ToCondition()).ToList();
        var any = AnyRows.Select(r => r.ToCondition()).ToList();
        var none = NotRows.Select(r => r.ToCondition()).ToList();

        if (all.Count == 1 && any.Count == 0 && none.Count == 0)
        {
            return all[0];
        }

        if (all.Count + any.Count + none.Count == 0)
        {
            return null;
        }

        return new RuleCondition
        {
            All = all.Count > 0 ? all : null,
            Any = any.Count > 0 ? any : null,
            Not = none.Count switch
            {
                0 => null,
                1 => none[0],
                _ => new RuleCondition { Any = none },
            },
        };
    }

    /// <summary>Fills the three lists from the rule's condition; false when it has a shape the form cannot show.</summary>
    private bool TryReadCondition(RuleCondition? when)
    {
        if (when is null)
        {
            return true;
        }

        static bool IsLeaf(RuleCondition c) => c.Field is not null && c.All is null && c.Any is null && c.Not is null;

        if (IsLeaf(when))
        {
            AllRows.Add(ConditionRow.From(when, Fields, row => AllRows.Remove(row)));
            return true;
        }

        if (when.Field is not null
            || (when.All?.All(IsLeaf) == false)
            || (when.Any?.All(IsLeaf) == false)
            || (when.Not is { } not && !(IsLeaf(not) || (not.Any?.All(IsLeaf) == true && not.All is null && not.Not is null && not.Field is null))))
        {
            return false;
        }

        foreach (var leaf in when.All ?? [])
        {
            AllRows.Add(ConditionRow.From(leaf, Fields, row => AllRows.Remove(row)));
        }

        foreach (var leaf in when.Any ?? [])
        {
            AnyRows.Add(ConditionRow.From(leaf, Fields, row => AnyRows.Remove(row)));
        }

        foreach (var leaf in when.Not is null ? [] : IsLeaf(when.Not) ? [when.Not] : when.Not.Any ?? [])
        {
            NotRows.Add(ConditionRow.From(leaf, Fields, row => NotRows.Remove(row)));
        }

        return true;
    }
}
