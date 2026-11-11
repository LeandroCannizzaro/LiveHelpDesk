using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.Core.Notifications;

namespace LiveHelpDesk.App.Views;

public partial class NotificationsView : UserControl
{
    private NotificationsViewModel? _model;
    private TextEditor _lastEditor;

    public NotificationsView()
    {
        InitializeComponent();
        _lastEditor = BodyEditor;

        TagEditing.Attach(TitleEditor, singleLine: true);
        TagEditing.Attach(BodyEditor, singleLine: false);
        TitleEditor.TextArea.GotFocus += (_, _) => _lastEditor = TitleEditor;
        BodyEditor.TextArea.GotFocus += (_, _) => _lastEditor = BodyEditor;

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in TemplateFields.All.GroupBy(f => f.Group))
        {
            var groupItem = new MenuItem { Header = group.Key };
            foreach (var field in group)
            {
                var item = new MenuItem { Header = $"{field.Label}   {{{{{field.Key}}}}}" };
                ToolTip.SetTip(item, field.Description);
                item.Click += (_, _) => TagEditing.Insert(_lastEditor, field.Key);
                groupItem.Items.Add(item);
            }

            flyout.Items.Add(groupItem);
        }

        InsertFieldButton.Flyout = flyout;
        ShowDocuments(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = DataContext as NotificationsViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }

        ShowDocuments(_model?.Selected);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationsViewModel.Selected))
        {
            ShowDocuments(_model?.Selected);
        }
    }

    // The editors are not bound: a null document would leave AvaloniaEdit without anything to edit.
    private void ShowDocuments(TemplateViewModel? template)
    {
        TitleEditor.Document = template?.TitleDocument ?? new TextDocument();
        BodyEditor.Document = template?.BodyDocument ?? new TextDocument();
    }

    private void OnBold(object? sender, RoutedEventArgs e) => TagEditing.Wrap(BodyEditor, "**", "**");

    private void OnItalic(object? sender, RoutedEventArgs e) => TagEditing.Wrap(BodyEditor, "_", "_");

    private void OnStrike(object? sender, RoutedEventArgs e) => TagEditing.Wrap(BodyEditor, "~~", "~~");

    private void OnList(object? sender, RoutedEventArgs e) => TagEditing.PrefixLines(BodyEditor, "- ");

    private void OnLink(object? sender, RoutedEventArgs e)
    {
        if (BodyEditor.SelectionLength == 0)
        {
            TagEditing.Wrap(BodyEditor, "[Apri il ticket", $"]({{{{{TemplateFields.Link}}}}})");
        }
        else
        {
            TagEditing.Wrap(BodyEditor, "[", $"]({{{{{TemplateFields.Link}}}}})");
        }
    }
}
