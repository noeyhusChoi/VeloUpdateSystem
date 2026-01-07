using System.Configuration;
using System.Data;
using System.Windows;
using Velopack;

namespace VeloUpdateSystem
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            VelopackApp.Build()
                .SetArgs(e.Args)
                .Run();

            base.OnStartup(e);
        }
    }

}
