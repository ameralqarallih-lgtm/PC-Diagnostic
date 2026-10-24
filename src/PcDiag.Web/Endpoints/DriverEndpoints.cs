using Microsoft.AspNetCore.Http.Features;
using PcDiag.Core.Configuration;
using PcDiag.Core.Ipc;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// Driver packages (screens and sensors): listed; imported from a zip sent as the request body (kept in the uploads
/// folder only until the recorder has copied and checked it); approved by their content or withdrawn; removed. And
/// Windows drivers for devices that have none: listed with the devices, imported, installed (confirmed by the person at
/// the PC, on its own screen) and removed. Everything but the lists works only on the PC itself (the security
/// middleware refuses it from other devices).
/// </summary>
internal static class DriverEndpoints
{
    /// <summary>The largest package accepted (the recorder checks again).</summary>
    public const long MaxUploadBytes = 64L * 1024 * 1024;

    public static IEndpointRouteBuilder MapDriverEndpoints(this IEndpointRouteBuilder app)
    {
        var drivers = app.MapGroup("/api/v1/drivers");

        // Windows drivers for devices that have none (before /{id}, which would take "windows").
        var windows = drivers.MapGroup("/windows");
        windows.MapGet(string.Empty, async (IAgentClient agent, CancellationToken ct) =>
            WindowsResult(agent, await agent.SendAsync(AgentCommands.WindowsDrivers, null, ct).ConfigureAwait(false)));
        windows.MapPost("/import", async (HttpContext context, DataPaths paths, PcDiag.Storage.Files.FileStore store, IAgentClient agent, CancellationToken ct) =>
            await UploadAsync(context, paths, store, async file => WindowsResult(agent, await agent.SendAsync(AgentCommands.WindowsDriverImport, new AgentRequestArgs(Value: file), ct).ConfigureAwait(false)), ct).ConfigureAwait(false));
        windows.MapPost("/{id}/install", async (string id, WindowsInstall body, IAgentClient agent, CancellationToken ct) =>
            WindowsResult(agent, await agent.SendAsync(AgentCommands.WindowsDriverInstall, new AgentRequestArgs(DriverId: id, Value: body.Device, On: body.RestorePoint), ct).ConfigureAwait(false)));
        windows.MapDelete("/{id}", async (string id, IAgentClient agent, CancellationToken ct) =>
            WindowsResult(agent, await agent.SendAsync(AgentCommands.WindowsDriverRemove, new AgentRequestArgs(DriverId: id), ct).ConfigureAwait(false)));

        drivers.MapGet(string.Empty, async (IAgentClient agent, CancellationToken ct) =>
            Result(agent, await agent.SendAsync(AgentCommands.OverlayScreenDrivers, null, ct).ConfigureAwait(false)));
        drivers.MapPost("/import", async (HttpContext context, DataPaths paths, PcDiag.Storage.Files.FileStore store, IAgentClient agent, CancellationToken ct) =>
            await UploadAsync(context, paths, store, async file => Result(agent, await agent.SendAsync(AgentCommands.DriverImport, new AgentRequestArgs(Value: file), ct).ConfigureAwait(false)), ct).ConfigureAwait(false));
        drivers.MapPost("/{id}/approve", async (string id, DriverApproval body, IAgentClient agent, CancellationToken ct) =>
            Result(agent, await agent.SendAsync(AgentCommands.OverlayScreenDriverApprove, new AgentRequestArgs(On: body.Approve, DriverId: id, Sha256: body.Sha256), ct).ConfigureAwait(false)));
        drivers.MapDelete("/{id}", async (string id, IAgentClient agent, CancellationToken ct) =>
            Result(agent, await agent.SendAsync(AgentCommands.DriverRemove, new AgentRequestArgs(DriverId: id), ct).ConfigureAwait(false)));

        return app;
    }

    private static async Task KeepAsync(PcDiag.Storage.Files.FileStore store, string file)
    {
        try
        {
            string sha;
            await using (var read = File.OpenRead(file))
            {
                sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(read).ConfigureAwait(false));
            }

            await using var content = File.OpenRead(file);
            await store.PutAsync(PcDiag.Storage.Files.FileKinds.Drivers, sha + ".zip", content, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            // The import itself succeeded; the copy is only a keepsake.
        }
    }

    /// <summary>
    /// Saves the request body (a zip, at most <see cref="MaxUploadBytes"/>) in the uploads folder, lets the recorder take
    /// it (<paramref name="send"/>), then deletes it.
    /// </summary>
    private static async Task<IResult> UploadAsync(HttpContext context, DataPaths paths, PcDiag.Storage.Files.FileStore store, Func<string, Task<IResult>> send, CancellationToken ct)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxUploadBytes + 1;
        }

        if (context.Request.ContentLength > MaxUploadBytes)
        {
            return TooLarge();
        }

        Directory.CreateDirectory(paths.DriverUploadsDirectory);
        var file = Path.Combine(paths.DriverUploadsDirectory, Guid.NewGuid().ToString("n") + ".zip");
        try
        {
            await using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxUploadBytes)
                    {
                        return TooLarge();
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                if (total == 0)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No package", detail: "Send the driver package (a zip) as the request body.");
                }
            }

            var result = await send(file).ConfigureAwait(false);
            if (result is not IStatusCodeHttpResult { StatusCode: >= 400 })
            {
                // Kept with the device drivers in the storage locations (named by its SHA-256), to install again later.
                await KeepAsync(store, file).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Removed with the next upload's tidy-up.
            }
        }
    }

    private static IResult TooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: $"A driver package is at most {MaxUploadBytes / (1024 * 1024)} MB.");

    private static IResult Result(IAgentClient agent, AgentResponse? response) => response switch
    {
        null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: agent.UnavailableReason),
        { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The driver package could not be changed", detail: response.Error),
        _ => Results.Ok(response.ScreenDrivers ?? []),
    };

    private static IResult WindowsResult(IAgentClient agent, AgentResponse? response) => response switch
    {
        null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: agent.UnavailableReason),
        { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The Windows driver could not be changed", detail: response.Error),
        _ => Results.Ok(response.WindowsDrivers),
    };
}

/// <summary>Installing a Windows driver package for a device (its instance id), with a restore point first or not.</summary>
public sealed record WindowsInstall(string Device, bool RestorePoint);
