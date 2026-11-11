using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Core.Notifications;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>
/// The "Notifiche" page: the texts that replace the standard notification for chosen rules and channels.
/// Like the rules, nothing takes effect until "Salva" and the service refuses a file with mistakes.
/// </summary>
public sealed partial class NotificationsViewModel(Func<ILiveHelpDeskApi?> api) : ObservableObject
{
    public const string DefaultTitle = "{{evento}}";

    public const string DefaultBody =
        "**{{titolo}}**\n\nPriorità: {{priorita}} · Stato: {{stato}}\n\n{{dettaglio}}\n\n[Apri il ticket]({{link}})";

    private IReadOnlyList<Choice> _rules = [];

    public ObservableCollection<TemplateViewModel> Templates { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public IReadOnlyList<Choice> PreviewChannels => RuleCatalog.Channels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private TemplateViewModel? _selected;

    [ObservableProperty]
    private Choice? _previewChannel;

    [ObservableProperty]
    private string _previewTitle = "";

    [ObservableProperty]
    private string _previewBody = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlainPreview))]
    private bool _isRichPreview;

    [ObservableProperty]
    private string _previewInfo = "";

    [ObservableProperty]
    private string _conflicts = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSelection => Selected is not null;

    public bool HasErrors => Errors.Count > 0;

    public bool IsPlainPreview => !IsRichPreview;

    public bool HasConflicts => Conflicts.Length > 0;

    /// <summary>Reads the rules (for their names) and the texts from the service, dropping unsaved edits.</summary>
    public async Task LoadAsync()
    {
        if (api() is not { } service)
        {
            Status = "Il servizio non è raggiungibile.";
            return;
        }

        try
        {
            var rules = RulesConfig.Parse(await service.GetRulesJsonAsync());
            _rules = rules.File is null
                ? []
                : [.. rules.File.Rules.Select(r => new Choice(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name))];
            Apply(await service.GetTemplatesJsonAsync());
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Status = ex is StreamJsonRpc.RemoteMethodNotFoundException
                ? "Il servizio installato non gestisce i testi di notifica: aggiornarlo."
                : $"Impossibile leggere i testi di notifica: {ex.Message}";
        }
    }

    private void Apply(string json)
    {
        var result = TemplatesConfig.Parse(json);
        Templates.Clear();
        foreach (var template in result.File?.Templates ?? [])
        {
            Add(new TemplateViewModel(template, _rules));
        }

        Selected = Templates.FirstOrDefault();
        ReportErrors(result.Errors);
        IsDirty = false;
        Status = result.File is null
            ? "Il file dei testi non è leggibile: salvando lo si sostituisce."
            : Templates.Count == 0 ? "Nessun testo personalizzato: le notifiche usano il testo standard." : "";
    }

    private void Add(TemplateViewModel template)
    {
        template.Edited += (sender, _) =>
        {
            IsDirty = true;
            if (sender == Selected)
            {
                Refresh();
            }
        };
        Templates.Add(template);
    }

    /// <summary>The whole texts file as it would be saved now.</summary>
    public string BuildJson()
    {
        var taken = Templates.Select(t => t.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        foreach (var template in Templates)
        {
            template.EnsureId(taken);
            taken.Add(template.Id);
        }

        return TemplatesConfig.Serialize(new TemplatesFile { Templates = [.. Templates.Select(t => t.ToTemplate())] });
    }

    partial void OnSelectedChanged(TemplateViewModel? value)
    {
        if (value is not null && (PreviewChannel is null || !value.SelectedChannels.Contains(PreviewChannel.Value, StringComparer.OrdinalIgnoreCase)))
        {
            var first = value.SelectedChannels.FirstOrDefault();
            PreviewChannel = PreviewChannels.FirstOrDefault(c => c.Value == first) ?? PreviewChannel ?? PreviewChannels.FirstOrDefault();
        }

        Refresh();
    }

    partial void OnPreviewChannelChanged(Choice? value) => Refresh();

    partial void OnConflictsChanged(string value) => OnPropertyChanged(nameof(HasConflicts));

    /// <summary>Rebuilds the preview and the overlap warning for the selected text.</summary>
    private void Refresh()
    {
        if (Selected is not { } template)
        {
            PreviewTitle = PreviewBody = PreviewInfo = Conflicts = "";
            return;
        }

        var values = new Dictionary<string, string?>(TemplateFields.Samples, StringComparer.OrdinalIgnoreCase);
        if (_rules.FirstOrDefault(r => template.SelectedRuleIds.Contains(r.Value, StringComparer.OrdinalIgnoreCase)) is { } rule)
        {
            values["regola"] = rule.Label;
        }

        var title = template.TitleDocument.Text.ReplaceLineEndings(" ");
        var body = template.BodyDocument.Text;
        var channel = PreviewChannel?.Value ?? "toast";
        var format = TemplateRenderer.FormatFor(channel);

        PreviewTitle = string.IsNullOrWhiteSpace(title) ? "(titolo standard)" : TemplateRenderer.RenderTitle(title, values);
        IsRichPreview = format != TemplateFormat.Plain;
        if (string.IsNullOrWhiteSpace(body))
        {
            PreviewBody = "(testo standard)";
            PreviewInfo = "";
        }
        else
        {
            PreviewBody = IsRichPreview
                ? TemplateRenderer.ExpandMarkdown(body, values)
                : TemplateRenderer.RenderBody(body, values, TemplateFormat.Plain);
            PreviewInfo = channel switch
            {
                "sms" => $"{PlainLength(body, values)} caratteri: un SMS ne mostra al massimo 320, il resto viene tagliato.",
                "whatsapp" => $"{PlainLength(body, values)} caratteri: WhatsApp ne mostra al massimo 1000.",
                "toast" or "overlay" => "Il toast e l'overlay mostrano testo semplice: la formattazione viene tolta.",
                "teams" => "Teams mostra grassetto, corsivo, elenchi e link.",
                "email" => "L'e-mail è in HTML, con una versione solo testo per i client che non lo leggono.",
                _ => "",
            };
        }

        Conflicts = string.Join("\n", Overlaps(template));
    }

    private IEnumerable<string> Overlaps(TemplateViewModel template)
    {
        var mine = template.SelectedRuleIds.SelectMany(r => template.SelectedChannels.Select(c => (r, c))).ToHashSet();
        foreach (var other in Templates.Where(t => t != template))
        {
            var shared = other.SelectedRuleIds.SelectMany(r => other.SelectedChannels.Select(c => (r, c))).Where(mine.Contains).ToList();
            if (shared.Count > 0)
            {
                var pairs = shared.Select(p => $"{RuleCatalog.Label(_rules, p.r)} su {RuleCatalog.Label(RuleCatalog.Channels, p.c)}");
                yield return $"Già coperto da «{other.Summary}»: {string.Join(", ", pairs)}.";
            }
        }
    }

    private static int PlainLength(string body, IReadOnlyDictionary<string, string?> values) =>
        TemplateRenderer.RenderBody(body, values, TemplateFormat.Plain).Length;

    [RelayCommand]
    private void AddTemplate()
    {
        var template = new TemplateViewModel(
            new NotificationTemplate
            {
                Name = $"Testo {Templates.Count + 1}",
                Title = DefaultTitle,
                Body = DefaultBody,
            },
            _rules);
        Add(template);
        Selected = template;
        IsDirty = true;
    }

    [RelayCommand]
    private void DuplicateTemplate()
    {
        if (Selected is not { } source)
        {
            return;
        }

        var copy = source.ToTemplate();
        var template = new TemplateViewModel(
            new NotificationTemplate
            {
                Name = $"{copy.Name} (copia)",
                RuleIds = copy.RuleIds,
                Title = copy.Title,
                Body = copy.Body,
            },
            _rules);
        Add(template);
        Selected = template;
        IsDirty = true;
        Status = "Copia creata senza canali: sceglierli, perché una regola su un canale può avere un solo testo.";
    }

    [RelayCommand]
    private void RemoveTemplate()
    {
        if (Selected is not { } template)
        {
            return;
        }

        var index = Templates.IndexOf(template);
        Templates.Remove(template);
        Selected = Templates.Count == 0 ? null : Templates[Math.Min(index, Templates.Count - 1)];
        IsDirty = true;
    }

    [RelayCommand]
    private async Task DiscardAsync() => await LoadAsync();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (api() is not { } service)
        {
            Status = "Il servizio non è raggiungibile: nulla è stato salvato.";
            return;
        }

        var json = BuildJson();
        var local = TemplatesConfig.Parse(json);
        if (!local.IsValid)
        {
            ReportErrors(local.Errors);
            Status = "Non salvato: correggere gli errori.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await service.SaveTemplatesJsonAsync(json);
            if (result.Saved)
            {
                var selectedId = Selected?.Id;
                Apply(await service.GetTemplatesJsonAsync());
                Selected = Templates.FirstOrDefault(t => t.Id == selectedId) ?? Templates.FirstOrDefault();
                Status = $"Salvato alle {DateTime.Now:HH:mm:ss}: i nuovi testi sono già attivi.";
            }
            else
            {
                ReportErrors(result.Errors);
                Status = "Non salvato: il servizio ha trovato degli errori.";
            }
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Status = ex is StreamJsonRpc.RemoteMethodNotFoundException
                ? "Non salvato: il servizio installato non gestisce i testi di notifica, aggiornarlo."
                : $"Non salvato: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportErrors(IEnumerable<string> errors)
    {
        Errors.Clear();
        foreach (var error in errors)
        {
            Errors.Add(error);
        }

        OnPropertyChanged(nameof(HasErrors));
    }

    private static bool IsRemote(Exception ex) =>
        ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.RemoteMethodNotFoundException
            or StreamJsonRpc.ConnectionLostException or IOException;
}
