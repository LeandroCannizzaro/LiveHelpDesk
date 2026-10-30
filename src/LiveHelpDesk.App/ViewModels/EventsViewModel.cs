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
            foreach (var evt in events)
            {
                Rows.Add(new EventRow(evt, open, AcknowledgeRowAsync));
            }

            Message = events.Count == 0 ? "Nessun evento finora." : "";
        }
        catch (Exception ex) when (ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException)
        {
            Message = $"Impossibile leggere gli eventi: {ex.Message}";
        }
    }

    private async Task AcknowledgeRowAsync(EventRow row)
    {
        await acknowledge(row.Key);
        row.Acknowledged = true;
    }
}
