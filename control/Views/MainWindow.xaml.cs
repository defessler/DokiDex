using System.Windows;
using DokiCode.Control.Models;
using DokiCode.Control.ViewModels;

namespace DokiCode.Control.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DashboardView _dashboard;
    private readonly LogsView _logs;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        _vm.ConfirmRequested += OnConfirmRequested;

        _dashboard = new DashboardView { DataContext = _vm };
        _logs = new LogsView { DataContext = _vm };
        PageHost.Content = _dashboard;

        Loaded += (_, _) => _vm.Start();
        Closed += (_, _) => _vm.Shutdown();
    }

    private void NavDashboard(object sender, RoutedEventArgs e) => PageHost.Content = _dashboard;
    private void NavLogs(object sender, RoutedEventArgs e) => PageHost.Content = _logs;

    private void OnConfirmRequested(ConfirmInfo info)
    {
        var dlg = new ConfirmWindow(info) { Owner = this };
        if (dlg.ShowDialog() == true) info.OnConfirmed();
    }
}
