using System.Windows;
using System.Windows.Controls;
using DokiCode.Control.ViewModels;

namespace DokiCode.Control.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _logs;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_logs != null) _logs.LineAppended -= ScrollToEnd;
        _logs = (DataContext as MainViewModel)?.Logs;
        if (_logs != null) _logs.LineAppended += ScrollToEnd;
    }

    private void ScrollToEnd()
    {
        if (_logs == null || _logs.Paused) return;
        var count = LogList.Items.Count;
        if (count > 0) LogList.ScrollIntoView(LogList.Items[count - 1]);
    }
}
