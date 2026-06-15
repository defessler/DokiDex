using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DokiDex.Control.ViewModels;

namespace DokiDex.Control.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    // live hover-preview of a mode switch in the explainer box, before any click
    private void ModeHover(object sender, MouseEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is FrameworkElement c && c.Tag is string mode)
            vm.HoverMode = mode;
    }

    private void ModeUnhover(object sender, MouseEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.HoverMode = "";
    }
}
