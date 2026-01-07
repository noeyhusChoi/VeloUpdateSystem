using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UpdateAgent;

public static class Program
{
    public static void Main(string[] args)
    {
        Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureServices((context, services) =>
            {
                services.Configure<UpdateAgentOptions>(
                    context.Configuration.GetSection(UpdateAgentOptions.SectionName));
                services.AddSingleton<AgentState>();
                services.AddHostedService<UpdateAgentService>();
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
