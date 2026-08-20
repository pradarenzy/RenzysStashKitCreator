using System.Windows;
namespace StashKitMaker.App;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        WindowsShellIdentity.SetProcessIdentity();
        base.OnStartup(e);
    }
}
