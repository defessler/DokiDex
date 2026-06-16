using System.Windows;
using DokiDex.Control.ViewModels;

namespace DokiDex.Control.Views;

// The Setup Wizard window. Shown via ShowDialog() from App.OnStartup when no DokiDex home resolves; the VM
// raises CloseRequested(true) once it has persisted an InstallRoot (fresh install done, or adopt), else (false)
// the user cancelled and the app exits.
public partial class InstallerWindow : Window
{
    public InstallerWindow()
    {
        InitializeComponent();
        var vm = new InstallerViewModel(Dispatcher);
        DataContext = vm;
        vm.CloseRequested += ok => { try { DialogResult = ok; } catch { } Close(); };
        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();   // keep the live install log pinned to newest output
    }
}
