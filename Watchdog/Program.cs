using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace Watchdog;

public static class Program
{
    public static void Main(string[] args)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(logDir);

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDir, "watchdog_.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}]{NewLine}{Message:lj}{NewLine}{Exception}");

        if (Environment.UserInteractive)
        {
            loggerConfig = loggerConfig.WriteTo.Console(
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}]{NewLine}{Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = loggerConfig.CreateLogger();

        try
        {
            Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    services.Configure<WatchdogOptions>(
                        context.Configuration.GetSection(WatchdogOptions.SectionName));
                    services.AddSingleton<RestartPolicy>();
                    services.AddSingleton<ProcessController>();
                    services.AddHostedService<WatchdogService>();
                })
                .Build()
                .Run();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
