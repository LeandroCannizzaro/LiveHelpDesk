using System.Collections.ObjectModel;
using System.ComponentModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Core.Notifications;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>One notification text in the editor: which rules and channels it is for, its title and its body.</summary>
public sealed partial class TemplateViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _missingRules;

    public TemplateViewModel(NotificationTemplate template, IReadOnlyList<Choice> rules)
    {
        Id = template.Id;
        _name = template.Name;
        RuleChoices = [.. rules.Select(r => new SelectableItem(r, template.RuleIds.Contains(r.Value, StringComparer.OrdinalIgnoreCase)))];
        bool HasChannel(string value) => template.Channels.Contains(value, StringComparer.OrdinalIgnoreCase);
        ChannelChoices = [.. RuleCatalog.Offered(RuleCatalog.Channels, HasChannel).Select(c => new SelectableItem(c, HasChannel(c.Value)))];
        _missingRules = [.. template.RuleIds.Where(id => !rules.Any(r => r.Value.Equals(id, StringComparison.OrdinalIgnoreCase)))];
        TitleDocument = new TextDocument(template.Title);
        BodyDocument = new TextDocument(template.Body);

        foreach (var item in RuleChoices.Concat(ChannelChoices))
        {
            item.PropertyChanged += OnChoiceChanged;
        }

        TitleDocument.TextChanged += (_, _) => Changed();
        BodyDocument.TextChanged += (_, _) => Changed();
    }

    /// <summary>Raised on every change, so the page knows there is something to save and can refresh the preview.</summary>
    public event EventHandler? Edited;

    public string Id { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _name;

    public ObservableCollection<SelectableItem> RuleChoices { get; }

    public ObservableCollection<SelectableItem> ChannelChoices { get; }

    public TextDocument TitleDocument { get; }

    public TextDocument BodyDocument { get; }

    public string Summary => string.IsNullOrWhiteSpace(Name) ? "(senza nome)" : Name;

    /// <summary>"3 regole · E-mail, Teams": what the text applies to, for the list.</summary>
    public string Coverage
    {
        get
        {
            var rules = RuleChoices.Count(r => r.IsSelected);
            var channels = ChannelChoices.Where(c => c.IsSelected).Select(c => c.Label).ToList();
            var ruleText = rules switch { 0 => "nessuna regola", 1 => "1 regola", _ => $"{rules} regole" };
            return $"{ruleText} · {(channels.Count == 0 ? "nessun canale" : string.Join(", ", channels))}";
        }
    }

    /// <summary>Rules this text pointed at that no longer exist; they are dropped when saving.</summary>
    public string? Warning => _missingRules.Count == 0
        ? null
        : $"Regole non più esistenti, verranno tolte al salvataggio: {string.Join(", ", _missingRules)}";

    public bool HasWarning => Warning is not null;

    public IEnumerable<string> SelectedRuleIds => RuleChoices.Where(r => r.IsSelected).Select(r => r.Value);

    public IEnumerable<string> SelectedChannels => ChannelChoices.Where(c => c.IsSelected).Select(c => c.Value);

    public void EnsureId(ICollection<string> taken)
    {
        while (string.IsNullOrWhiteSpace(Id) || taken.Contains(Id))
        {
            Id = $"testo-{Guid.NewGuid():N}"[..14];
        }
    }

    public NotificationTemplate ToTemplate() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        RuleIds = [.. SelectedRuleIds],
        Channels = [.. SelectedChannels],
        Title = TitleDocument.Text.ReplaceLineEndings(" ").Trim(),
        Body = BodyDocument.Text.ReplaceLineEndings("\n").TrimEnd(),
    };

    partial void OnNameChanged(string value) => Changed();

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableItem.IsSelected))
        {
            OnPropertyChanged(nameof(Coverage));
            Changed();
        }
    }

    private void Changed() => Edited?.Invoke(this, EventArgs.Empty);
}
