using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.ViewModels;

public sealed partial class EventRow : ObservableObject
{
    private readonly Func<EventRow, Task> _open;
    private readonly Func<EventRow, Task> _acknowledge;

    public EventRow(EventInfo info, Func<EventRow, Task> open, Func<EventRow, Task> acknowledge)
    {
        _open = open;
        _acknowledge = acknowledge;
        Key = info.Key;
        When = info.OccurredAt.ToLocalTime().ToString("dd/MM HH:mm");
        Label = EventLabels.Describe(info.Type, info.Subtype);
        Number = info.TicketNumber is null ? "" : $"#{info.TicketNumber}";
        Title = info.Title ?? "";
        Url = info.Url;
        _acknowledged = info.Acknowledged;
    }

    public string Key { get; }

    public string When { get; }

    public string Label { get; }

    public string Number { get; }

    public string Title { get; }

    public Uri? Url { get; }

    public bool CanOpen => Url is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcknowledge))]
    private bool _acknowledged;

    public bool CanAcknowledge => !Acknowledged;

    [RelayCommand]
    private Task OpenAsync() => _open(this);

    [RelayCommand]
    private Task AcknowledgeAsync() => _acknowledge(this);
}

/// <summary>The recent events of this user's monitor, newest first.</summary>
public sealed partial class EventsViewModel(Func<ILiveHelpDeskApi?> api, Func<EventRow, Task> open, Func<string, Task> acknowledge) : ObservableObject
{
    // "Pulisci" only clears the screen: the service keeps its events (they also stop duplicates), the app stops showing them.
    private readonly HashSet<string> _cleared = [];

    public ObservableCollection<EventRow> Rows { get; } = [];

    [ObservableProperty]
    private string _message = "In attesa del servizio…";

    public async Task RefreshAsync()
    {
        if (api() is not { } service)
        {
            Message = "Il servizio non è raggiungibile.";
            return;
        }

        try
        {
            var events = await service.GetRecentEventsAsync(100);
            Rows.Clear();
            foreach (var evt in events.Where(e => !_cleared.Contains(e.Key)))
            {
                Rows.Add(new EventRow(evt, open, AcknowledgeRowAsync));
            }

            ShowEmptyMessage(events.Count);
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException)
        {
            Message = $"Impossibile leggere gli eventi: {ex.Message}";
        }
    }

    /// <summary>"Visti tutti": every event in the list not seen yet is marked as seen.</summary>
    [RelayCommand]
    private async Task AcknowledgeAllAsync()
    {
        foreach (var row in Rows.Where(r => !r.Acknowledged).ToList())
        {
            await AcknowledgeRowAsync(row);
        }
    }

    /// <summary>"Pulisci": empties the list on screen; only events that arrive afterwards show up.</summary>
    [RelayCommand]
    private void Clear()
    {
        foreach (var row in Rows)
        {
            _cleared.Add(row.Key);
        }

        Rows.Clear();
        ShowEmptyMessage(_cleared.Count);
    }

    private void ShowEmptyMessage(int total) =>
        Message = Rows.Count > 0 ? "" : total == 0 ? "Nessun evento finora." : "Nessun evento nuovo dall'ultima pulizia.";

    private async Task AcknowledgeRowAsync(EventRow row)
    {
        await acknowledge(row.Key);
        row.Acknowledged = true;
    }
}
