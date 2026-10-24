using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PcDiag.Core.Abstractions;
using PcDiag.Engine.Ipc;
using PcDiag.Storage.Sqlite;

namespace PcDiag.Agent.Tests;

public sealed class AgentHostTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        new SqliteConnectionFactory().ReleasePooledConnectionsUnder(_dataDirectory);
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Builds the real Agent composition and resolves everything the host starts. A dependency cycle hidden behind a
    /// factory registration does not throw; it recurses forever, so the resolution runs with a deadline.
    /// </summary>
    [Fact]
    public async Task Every_service_the_agent_starts_can_be_resolved()
    {
        var builder = AgentHost.CreateBuilder([$"--Storage:DataDirectory={_dataDirectory}", "--Agent:ShowTrayIcon=false", "--Agent:CaptureHotkey="]);
        using var host = builder.Build();

        var resolve = Task.Run(() =>
        {
            var hosted = host.Services.GetServices<IHostedService>().ToList();
            hosted.ShouldContain(s => s is PcDiag.Engine.Anomalies.AnomalyEngine);
            hosted.ShouldContain(s => s is PcDiag.Engine.Anomalies.TriggerService);
            hosted.ShouldContain(s => s is PcDiag.Engine.Recording.EnhancedModeService);
            host.Services.GetServices<PcDiag.Core.Abstractions.IFlightRecorder>().ShouldHaveSingleItem();
            host.Services.GetServices<PcDiag.Core.Abstractions.IProcessIoMonitor>().ShouldHaveSingleItem();
            host.Services.GetServices<ISampleConsumer>().ShouldContain(c => c is PcDiag.Engine.Anomalies.AnomalyEngine);
            host.Services.GetRequiredService<AgentCommandHandler>();
            host.Services.GetRequiredService<PcDiag.Engine.Diagnostics.IncidentDiagnostics>();

        });

        var finished = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(30)));
        finished.ShouldBe(resolve, "Resolving the Agent's services did not finish: look for a dependency cycle through a factory registration.");
        await resolve;
    }

    [Fact]
    public void The_agent_contains_no_http_or_socket_code()
    {
        // The Agent may run elevated; it talks to the Web host only through the per-user pipe and files.
        var references = typeof(AgentHost).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        references.ShouldNotContain("System.Net.Http");
        references.ShouldNotContain("System.Net.Sockets");
        references.ShouldNotContain("System.Net.HttpListener");
        references.ShouldNotContain(n => n!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
