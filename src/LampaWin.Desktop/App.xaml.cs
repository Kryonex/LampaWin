using System.Windows;

namespace LampaWin.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await DesktopBootstrap.RunAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить LampaWin.\n\n{ex.Message}", "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
