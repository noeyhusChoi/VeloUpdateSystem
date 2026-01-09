using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Watchdog;

public static class Program
{
    public static void Main(string[] args)
    {
        Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureServices((context, services) =>
            {
                services.Configure<WatchdogOptions>(
                    context.Configuration.GetSection(WatchdogOptions.SectionName));
                services.AddHostedService<WatchdogService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .Build()
            .Run();
    }
}
