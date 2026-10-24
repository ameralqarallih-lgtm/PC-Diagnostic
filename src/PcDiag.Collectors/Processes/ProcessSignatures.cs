using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Processes;

namespace PcDiag.Collectors.Processes;

/// <summary>Checks a program file's signature (a seam for tests).</summary>
public interface IFileSignatureVerifier
{
    /// <summary>Checks <paramref name="path"/>.</summary>
    FileSignature Verify(string path);
}

/// <summary>WinVerifyTrust, embedded then catalog signatures.</summary>
public sealed class WindowsFileSignatureVerifier : IFileSignatureVerifier
{
    /// <inheritdoc />
    public FileSignature Verify(string path) => FileSignatures.Verify(path);
}

/// <summary>
/// Signature checks for process images, off the sampling path: requests are queued and checked one at a time on a
/// below-normal-priority thread, at most <see cref="MaxChecksPerMinute"/> a minute (hashing reads the whole file), and
/// remembered per path, size and last write time, so each program is checked once per version. Files larger than
/// <see cref="MaxFileBytes"/> are not hashed (Unknown).
/// </summary>
public sealed partial class ProcessSignatures : IDisposable
{
    /// <summary>Most checks per minute.</summary>
    public const int MaxChecksPerMinute = 60;

    /// <summary>Largest file checked.</summary>
    public const long MaxFileBytes = 512L << 20;

    /// <summary>Most files remembered.</summary>
    public const int MaxCached = 4096;

    private readonly IFileSignatureVerifier _verifier;
    private readonly ILogger<ProcessSignatures> _logger;
    private readonly ConcurrentDictionary<FileVersion, FileSignature> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<string> _requests = new(new ConcurrentQueue<string>(), boundedCapacity: 2048);
    private readonly ConcurrentQueue<(string Path, FileSignature Signature)> _completed = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _spacing;
    private Thread? _worker;

    /// <summary>Creates the checker; its thread starts with the first request.</summary>
    public ProcessSignatures(IFileSignatureVerifier verifier, ILogger<ProcessSignatures> logger)
        : this(verifier, logger, TimeSpan.FromMinutes(1) / MaxChecksPerMinute)
    {
    }

    /// <summary>Creates the checker with another pause between checks (tests).</summary>
    internal ProcessSignatures(IFileSignatureVerifier verifier, ILogger<ProcessSignatures> logger, TimeSpan spacing)
    {
        _verifier = verifier;
        _logger = logger;
        _spacing = spacing;
    }

    /// <summary>A remembered result for the file as it is now, if there is one.</summary>
    public bool TryGet(string path, out FileSignature signature)
    {
        signature = null!;
        return FileVersion.Of(path) is { } version && _cache.TryGetValue(version, out signature!);
    }

    /// <summary>Queues a check (once per path while it is waiting).</summary>
    public void Request(string path)
    {
        if (!_queued.TryAdd(path, 0))
        {
            return;
        }

        if (!_requests.TryAdd(path))
        {
            _queued.TryRemove(path, out _);
            return;
        }

        EnsureWorker();
    }

    /// <summary>Checks finished since the previous call.</summary>
    public IReadOnlyList<(string Path, FileSignature Signature)> TakeCompleted()
    {
        if (_completed.IsEmpty)
        {
            return [];
        }

        var done = new List<(string, FileSignature)>();
        while (_completed.TryDequeue(out var item))
        {
            done.Add(item);
        }

        return done;
    }

    /// <summary>Checks one file now (the worker's step; exposed for tests).</summary>
    internal FileSignature Check(string path)
    {
        var version = FileVersion.Of(path);
        if (version is null)
        {
            return new FileSignature(SignatureStatus.Unknown, null);
        }

        if (_cache.TryGetValue(version.Value, out var cached))
        {
            return cached;
        }

        var signature = version.Value.Length > MaxFileBytes ? new FileSignature(SignatureStatus.Unknown, null) : _verifier.Verify(path);
        if (_cache.Count >= MaxCached)
        {
            _cache.Clear();
        }

        _cache[version.Value] = signature;
        return signature;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _requests.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(5));
        _requests.Dispose();
        _stop.Dispose();
    }

    private void EnsureWorker()
    {
        lock (_gate)
        {
            if (_worker is not null)
            {
                return;
            }

            _worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PcDiag signature checks" };
            _worker.Start();
        }
    }

    private void Run()
    {
        try
        {
            foreach (var path in _requests.GetConsumingEnumerable(_stop.Token))
            {
                try
                {
                    _completed.Enqueue((path, Check(path)));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogCheckFailed(_logger, ex, path);
                    _completed.Enqueue((path, new FileSignature(SignatureStatus.Unknown, null)));
                }
                finally
                {
                    _queued.TryRemove(path, out _);
                }

                _stop.Token.WaitHandle.WaitOne(_spacing);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Signature check failed for {Path}")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception, string path);

    /// <summary>A file as it is now: a new build of a program gets a new check.</summary>
    private readonly record struct FileVersion(string Path, long Length, DateTime LastWriteUtc)
    {
        public static FileVersion? Of(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileVersion(path.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }
}
