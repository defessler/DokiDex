using System.Windows;
using DokiDex.Control.Models;

namespace DokiDex.Control.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(ConfirmInfo info)
    {
        InitializeComponent();
        TitleText.Text = info.Title;
        StopList.ItemsSource = info.WillStop;
        StartList.ItemsSource = info.WillStart;
        HeadroomText.Text = info.HeadroomText;
        ConfirmBtn.Content = info.Title.Replace("?", "");
        if (!info.Fits) ConfirmBtn.IsEnabled = false;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
