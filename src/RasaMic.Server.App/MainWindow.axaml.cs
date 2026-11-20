using System.ComponentModel;
using Avalonia.Controls;
using RasaMic.Server.App.ViewModels;

namespace RasaMic.Server.App;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow(MainWindowViewModel? viewModel = null)
    {
        InitializeComponent();
        ViewModel = viewModel ?? new MainWindowViewModel();
        DataContext = ViewModel;
    }

    public MainWindowViewModel ViewModel { get; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        ViewModel.Shutdown();
        Close();
    }
}