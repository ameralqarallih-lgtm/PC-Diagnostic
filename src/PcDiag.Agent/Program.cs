using Microsoft.Extensions.Options;
using PcDiag.Agent;

// Exit codes: 0 = clean shutdown, 2 = another Agent is already running in this session, 3 = invalid configuration.
// As a command client (see CommandLineClient): 0 done, 1 refused, 4 no Agent running, 64 usage error.
// "open": 0 the dashboard was opened (or its link offered), 1 it could not be opened.
NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);

// "PcDiag.Agent.exe storage-host": the file storage's upkeep, started by the recorder without administrator rights.
if (PcDiag.Agent.Storage.StorageHostMode.IsCommand(args))
{
    return await PcDiag.Agent.Storage.StorageHostMode.RunAsync(args);
}

// "PcDiag.Agent.exe screen-host": the USB screens' driver host, started by the recorder without administrator rights.
if (PcDiag.Agent.Screens.ScreenHostMode.IsCommand(args))
{
    return await PcDiag.Agent.Screens.ScreenHostMode.RunAsync(args);
}

// "PcDiag.Agent.exe open" (the Start menu shortcut): start the recorder if needed and open the dashboard.
if (OpenCommand.IsCommand(args))
{
    return await OpenCommand.RunAsync(args);
}

// "PcDiag.Agent.exe capture ScreenFlicker": talk to the running Agent instead of starting one.
if (CommandLineClient.IsCommand(args))
{
    return await CommandLineClient.RunAsync(args);
}

using var instance = SingleInstance.TryAcquire(@"Local\PcDiagnostic.Agent");
if (instance is null)
{
    await Console.Error.WriteLineAsync("PC Diagnostic Agent is already running in this session.");
    return 2;
}

var builder = AgentHost.CreateBuilder(args);

try
{
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (OptionsValidationException ex)
{
    await Console.Error.WriteLineAsync("PC Diagnostic Agent could not start because the configuration is invalid:");
    foreach (var failure in ex.Failures)
    {
        await Console.Error.WriteLineAsync("  - " + failure);
    }

    return 3;
}
