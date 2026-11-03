using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Core;
using LiveHelpDesk.Core.Rules;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>What the preview found for one rule.</summary>
public sealed record PreviewRow(string RuleName, string Matches, string Examples);

/// <summary>
/// The rules editor: a form per rule for the common cases, the whole file as JSON for the rest, and a preview that
/// tries the unsaved rules on today's tickets. Nothing takes effect until "Salva", and the service refuses a file
/// with mistakes, so a typo can never leave the monitor without rules.
/// </summary>
public sealed partial class RulesViewModel(Func<ILiveHelpDeskApi?> api, TicketMapping? mapping = null) : ObservableObject
{
    private readonly TicketMapping _mapping = mapping ?? TicketMapping.Qgs;
    private IReadOnlyList<FieldInfo> _fields = [];
    private string _savedJson = "";

    public ObservableCollection<RuleViewModel> Rules { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public ObservableCollection<PreviewRow> Preview { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RuleViewModel? _selected;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _jsonText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowForm))]
    private bool _showJson;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSelection => Selected is not null;

    public bool ShowForm => !ShowJson;

    public bool HasErrors => Errors.Count > 0;

    public bool HasPreview => Preview.Count > 0;

    /// <summary>Reads the rules (and the names of queues and users) from the service, dropping unsaved edits.</summary>
    public async Task LoadAsync()
    {
        if (api() is not { } service)
        {
            Status = "Il servizio non è raggiungibile.";
            return;
        }

        try
        {
            var lookups = await service.GetLookupsAsync();
            _fields = RuleCatalog.Fields(_mapping, lookups);
            Apply(await service.GetRulesJsonAsync(), markSaved: true);
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Status = $"Impossibile leggere le regole: {ex.Message}";
        }
    }

    private void Apply(string json, bool markSaved)
    {
        var result = RulesConfig.Parse(json);
        Errors.Clear();
        Preview.Clear();
        Rules.Clear();

        if (result.File is null)
        {
            // Unreadable: show the text so it can be mended by hand.
            JsonText = json;
            ShowJson = true;
            ReportErrors(result.Errors);
            return;
        }

        if (_fields.Count == 0)
        {
            _fields = RuleCatalog.Fields(_mapping, []);
        }

        foreach (var rule in result.File.Rules)
        {
            Add(new RuleViewModel(rule, _fields));
        }

        Selected = Rules.FirstOrDefault();
        ReportErrors(result.Errors);
        if (markSaved)
        {
            _savedJson = BuildJson();
            IsDirty = false;
        }
        else
        {
            IsDirty = true;
        }

        Status = Rules.Count == 0 ? "Nessuna regola: non arriverà nessuna notifica." : "";
    }

    private void Add(RuleViewModel rule)
    {
        rule.Edited += (_, _) => IsDirty = true;
        Rules.Add(rule);
    }

    /// <summary>The whole rules file as it would be saved now.</summary>
    public string BuildJson()
    {
        var taken = Rules.Select(r => r.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        foreach (var rule in Rules)
        {
            rule.EnsureId(taken);
            taken.Add(rule.Id);
        }

        return RulesConfig.Serialize(new RulesFile { Rules = [.. Rules.Select(r => r.ToRule())] });
    }

    [RelayCommand]
    private void AddRule()
    {
        var rule = new RuleViewModel(
            new Rule
            {
                Name = "Nuova regola",
                Events = [TicketEventType.TicketCreated],
                Actions = new RuleActions { Channels = ["toast", "overlay"], Severity = Severity.Attention },
            },
            _fields.Count > 0 ? _fields : RuleCatalog.Fields(_mapping, []));
        Add(rule);
        Selected = rule;
        IsDirty = true;
    }

    [RelayCommand]
    private void DuplicateRule()
    {
        if (Selected is null)
        {
            return;
        }

        var copy = Selected.ToRule();
        copy.Id = "";
        copy.Name = $"{copy.Name} (copia)";
        var viewModel = new RuleViewModel(copy, _fields);
        Add(viewModel);
        Selected = viewModel;
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (Selected is null)
        {
            return;
        }

        var index = Rules.IndexOf(Selected);
        Rules.Remove(Selected);
        Selected = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];
        IsDirty = true;
    }

    [RelayCommand]
    private void RestoreDefaults() => Apply(RulesConfig.Serialize(DefaultRules.Create()), markSaved: false);

    [RelayCommand]
    private async Task DiscardAsync() => await LoadAsync();

    /// <summary>Switches between the form and the JSON text, keeping them in step.</summary>
    [RelayCommand]
    private void ToggleJson()
    {
        if (!ShowJson)
        {
            JsonText = BuildJson();
            ShowJson = true;
            return;
        }

        ApplyJson();
    }

    [RelayCommand]
    private void ApplyJson()
    {
        var result = RulesConfig.Parse(JsonText);
        if (result.File is null)
        {
            ReportErrors(result.Errors);
            return; // stay on the text: there is nothing to build a form from yet
        }

        Apply(JsonText, markSaved: false);
        ShowJson = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (api() is not { } service)
        {
            Status = "Il servizio non è raggiungibile: nulla è stato salvato.";
            return;
        }

        var json = ShowJson ? JsonText : BuildJson();
        var local = RulesConfig.Parse(json);
        if (!local.IsValid)
        {
            ReportErrors(local.Errors);
            Status = "Non salvato: correggere gli errori.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await service.SaveRulesJsonAsync(json);
            if (result.Saved)
            {
                ReportErrors([]);
                Apply(await service.GetRulesJsonAsync(), markSaved: true);
                Status = $"Salvato alle {DateTime.Now:HH:mm:ss}: le nuove regole sono già attive.";
            }
            else
            {
                ReportErrors(result.Errors);
                Status = "Non salvato: il servizio ha trovato degli errori.";
            }
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Status = $"Non salvato: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Tries the rules as they are now (saved or not) on today's active tickets, as if each had just been opened.</summary>
    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (api() is not { } service)
        {
            Status = "Il servizio non è raggiungibile.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await service.DryRunDraftAsync(ShowJson ? JsonText : BuildJson());
            Preview.Clear();
            foreach (var rule in result.Rules)
            {
                var examples = result.Examples
                    .Where(e => e.RuleId == rule.RuleId)
                    .Select(e => $"#{e.TicketNumber} {Trim(e.Title)}");
                Preview.Add(new PreviewRow(
                    string.IsNullOrWhiteSpace(rule.RuleName) ? rule.RuleId : rule.RuleName,
                    rule.Matches == 0 ? "nessun ticket" : rule.Matches == 1 ? "1 ticket" : $"{rule.Matches} ticket",
                    string.Join("  ·  ", examples)));
            }

            OnPropertyChanged(nameof(HasPreview));
            Status = $"Prova su {result.TicketsEvaluated} ticket attivi, come se ognuno fosse appena arrivato.";
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Status = $"Prova non riuscita: {ex.Message}";
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

    private static string Trim(string? text) => text is { Length: > 45 } ? text[..45] + "…" : text ?? "";

    private static bool IsRemote(Exception ex) =>
        ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException;
}
