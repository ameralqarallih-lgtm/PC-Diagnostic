using PcDiag.Core.Configuration;
using PcDiag.Storage.Files;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// Where files are kept (Settings › Storage): the storage locations (This PC, folders, S3-compatible services), what
/// each keeps, a test before one is added, its keys (set, never shown again), its order, removing one (its files go to
/// the others first), the job bringing them up to date, and the files of each kind; the default location; and PC
/// Diagnostic Storage, SeaweedFS run on this PC (set up, started, stopped). Changes are made on this PC only.
/// </summary>
internal static class StorageLocationEndpoints
{
    public static IEndpointRouteBuilder MapStorageLocationEndpoints(this IEndpointRouteBuilder app)
    {
        var locations = app.MapGroup("/api/v1/storage/locations");

        locations.MapGet(string.Empty, (FileStore store) => Results.Ok(new LocationsResponse(store.Views(), store.Job, FileKinds.All, [.. FileKinds.OnThisPc])));

        locations.MapPost("/test", async (LocationRequest body, FileStore store, CancellationToken ct) =>
            Results.Ok(new TestResponse(await store.TestAsync(body.ToLocation(body.Id ?? "test"), body.Keys(), ct).ConfigureAwait(false))));

        locations.MapPost(string.Empty, async (LocationRequest body, FileStore store, CancellationToken ct) =>
        {
            var (location, problem) = await store.AddAsync(body.ToLocation("new"), body.Keys(), ct).ConfigureAwait(false);
            return location is null
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The location cannot be used", detail: problem)
                : Results.Ok(new LocationsResponse(store.Views(), store.Job, FileKinds.All, [.. FileKinds.OnThisPc]));
        });

        locations.MapPatch("/{id}", (string id, LocationChange body, FileStore store) =>
            store.Update(id, body.Name, body.Keeps, body.AccessKey is { Length: > 0 } access && body.SecretKey is { Length: > 0 } secret ? new StorageKeys(access, secret) : null) is { } problem
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The location was not changed", detail: problem)
                : Results.Ok(new LocationsResponse(store.Views(), store.Job, FileKinds.All, [.. FileKinds.OnThisPc])));

        locations.MapDelete("/{id}", async (string id, bool? force, FileStore store, CancellationToken ct) =>
            await store.RemoveAsync(id, ct, force == true).ConfigureAwait(false) is { } problem
                ? Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The location was not removed", detail: problem)
                : Results.Ok(new LocationsResponse(store.Views(), store.Job, FileKinds.All, [.. FileKinds.OnThisPc])));

        locations.MapPost("/order", (OrderRequest body, FileStore store) =>
        {
            store.Reorder(body.Ids ?? []);
            return Results.Ok(new LocationsResponse(store.Views(), store.Job, FileKinds.All, [.. FileKinds.OnThisPc]));
        });

        locations.MapPost("/sync", (FileStore store) =>
        {
            store.StartSync();
            return Results.Accepted();
        });

        // Where files are kept by default (it keeps every kind; with move, the previous default stops keeping them).
        locations.MapPost("/{id}/default", (string id, DefaultRequest? body, FileStore store) =>
            store.SetDefault(id, body?.Move ?? true) is { } problem
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The default was not changed", detail: problem)
                : Results.Ok(new LocationsResponse(store.Views(), store.Job, FileKinds.All, [.. FileKinds.OnThisPc])));

        // PC Diagnostic Storage: how it is, setting it up (the program downloaded once when not shipped), stopping and starting it.
        var local = app.MapGroup("/api/v1/storage/local");
        local.MapGet(string.Empty, (FileStore store, LocalStoreSetup setup, DataPaths paths) => Results.Ok(LocalView(store, setup, paths)));
        local.MapPost(string.Empty, (LocalRequest? body, FileStore store, LocalStoreSetup setup, DataPaths paths) =>
        {
            if (body?.Folder is { Length: > 0 } folder && LocalObjectStore.CheckFolder(folder) is { } bad)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That folder cannot be used", detail: bad);
            }

            return setup.Start(body?.Folder, body?.MakeDefault ?? true)
                ? Results.Accepted(value: LocalView(store, setup, paths))
                : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not set up again", detail: "PC Diagnostic Storage is set up already, or being set up.");
        });
        local.MapPost("/start", (FileStore store, LocalStoreSetup setup, DataPaths paths) => Stopped(store, setup, paths, false));
        local.MapPost("/stop", (FileStore store, LocalStoreSetup setup, DataPaths paths) => Stopped(store, setup, paths, true));

        app.MapGet("/api/v1/storage/files/{kind}", async (string kind, FileStore store, CancellationToken ct) =>
            FileKinds.All.Contains(kind)
                ? Results.Ok(await store.ListAsync(kind, ct).ConfigureAwait(false))
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such kind of file", detail: "Kinds: " + string.Join(", ", FileKinds.All)));

        return app;
    }

    private static IResult Stopped(FileStore store, LocalStoreSetup setup, DataPaths paths, bool stopped) =>
        store.SetLocalStopped(stopped) is { } problem
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not set up", detail: problem)
            : Results.Ok(LocalView(store, setup, paths));

    private static LocalStoreView LocalView(FileStore store, LocalStoreSetup setup, DataPaths paths)
    {
        var location = store.Locations.FirstOrDefault(l => l.Local is not null);
        var state = location is null ? null : LocalObjectStoreHost.Read(paths.Root);
        var program = LocalObjectStore.BundledExe() is not null ? "shipped" : File.Exists(LocalObjectStore.DownloadedExe(paths.Root)) ? "downloaded" : null;
        return new LocalStoreView(
            LocalObjectStore.Name,
            LocalObjectStore.Version,
            program,
            LocalObjectStore.DownloadBytes,
            LocalObjectStore.DefaultFolder(paths.Root),
            location?.Id,
            location?.Local?.Folder,
            location?.Local?.S3Port,
            state is { Running: true } && state.Port == location?.Local?.S3Port,
            location?.Local?.Stopped ?? false,
            location is null || location.Local!.Stopped ? null : state?.Problem,
            location?.Default ?? false,
            setup.State);
    }

    /// <summary>PC Diagnostic Storage as the page shows it.</summary>
    /// <param name="Program">"shipped" (installed with PC Diagnostic), "downloaded", or null (downloaded when set up).</param>
    internal sealed record LocalStoreView(
        string Name,
        string Version,
        string? Program,
        long DownloadBytes,
        string DefaultFolder,
        string? LocationId,
        string? Folder,
        int? Port,
        bool Running,
        bool Stopped,
        string? Problem,
        bool IsDefault,
        LocalStoreSetupState Setup);

    /// <summary>Setting it up: its folder (the default one when not given) and whether it becomes where files are kept by default.</summary>
    internal sealed record LocalRequest(string? Folder, bool? MakeDefault);

    /// <summary>Making a location the default: whether the previous default stops keeping every kind.</summary>
    internal sealed record DefaultRequest(bool? Move);

    /// <summary>The locations, the job, the kinds of file, and those kept on This PC always.</summary>
    internal sealed record LocationsResponse(IReadOnlyList<LocationView> Locations, StorageJob Job, IReadOnlyList<string> Kinds, IReadOnlyList<string> OnThisPc);

    /// <summary>A test's answer: null when it works.</summary>
    internal sealed record TestResponse(string? Problem);

    /// <summary>A location to test or add, with its keys for an S3 service.</summary>
    internal sealed record LocationRequest(
        string Kind,
        string Name,
        IReadOnlyList<string>? Keeps,
        string? Path = null,
        string? Endpoint = null,
        string? Bucket = null,
        string? Region = null,
        string? Prefix = null,
        bool PathStyle = true,
        string? AccessKey = null,
        string? SecretKey = null,
        string? Id = null)
    {
        public StorageLocation ToLocation(string id) =>
            new(id, Name?.Trim() ?? string.Empty, Kind, Keeps ?? [], Path?.Trim(), Endpoint?.Trim(), Bucket?.Trim(), Region?.Trim(), Prefix?.Trim(), PathStyle);

        public StorageKeys? Keys() => AccessKey is { Length: > 0 } access && SecretKey is { Length: > 0 } secret ? new StorageKeys(access.Trim(), secret.Trim()) : null;
    }

    /// <summary>A location's name, what it keeps, or new keys.</summary>
    internal sealed record LocationChange(string? Name, IReadOnlyList<string>? Keeps, string? AccessKey = null, string? SecretKey = null);

    /// <summary>The locations' order (This PC stays first).</summary>
    internal sealed record OrderRequest(IReadOnlyList<string>? Ids);
}
