using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace PcDiag.Storage.Files;

/// <summary>One storage location's files: put, open, delete, list, and a check that it works.</summary>
internal interface IStorageProvider : IDisposable
{
    Task PutAsync(string kind, string name, Stream content, string sha256, CancellationToken cancellationToken);

    /// <summary>The file's content, or null when this location does not have it.</summary>
    Task<Stream?> OpenAsync(string kind, string name, CancellationToken cancellationToken);

    Task DeleteAsync(string kind, string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredFile>> ListAsync(string kind, CancellationToken cancellationToken);

    /// <summary>Null when it works; otherwise what went wrong (it writes, reads and deletes a small probe).</summary>
    Task<string?> TestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A folder: each kind in a folder of its own (This PC keeps pictures where the recorder reads them). A file is written
/// to a temporary name first and moved into place, so a reader never sees half of it.
/// </summary>
internal sealed class FolderProvider(Func<string, string> folderOf) : IStorageProvider
{
    public async Task PutAsync(string kind, string name, Stream content, string sha256, CancellationToken cancellationToken)
    {
        var folder = folderOf(kind);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public Task<Stream?> OpenAsync(string kind, string name, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folderOf(kind), name);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true) : null);
    }

    public Task DeleteAsync(string kind, string name, CancellationToken cancellationToken)
    {
        File.Delete(Path.Combine(folderOf(kind), name));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredFile>> ListAsync(string kind, CancellationToken cancellationToken)
    {
        var folder = folderOf(kind);
        IReadOnlyList<StoredFile> files = Directory.Exists(folder)
            ? [.. new DirectoryInfo(folder).EnumerateFiles().Where(f => FileKinds.IsName(f.Name) && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).Select(f => new StoredFile(kind, f.Name, f.Length, f.LastWriteTimeUtc))]
            : [];
        return Task.FromResult(files);
    }

    public async Task<string?> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var name = "probe-" + Guid.NewGuid().ToString("N")[..12];
            using var probe = new MemoryStream("PC Diagnostic"u8.ToArray());
            await PutAsync(FileKinds.Backups, name, probe, string.Empty, cancellationToken).ConfigureAwait(false);
            await using (var read = await OpenAsync(FileKinds.Backups, name, cancellationToken).ConfigureAwait(false))
            {
                if (read is null)
                {
                    return "A file written there could not be read back.";
                }
            }

            await DeleteAsync(FileKinds.Backups, name, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// An S3-compatible service (AWS, MinIO, R2, Wasabi, B2, SeaweedFS): each file an object "prefix/kind/name", with its
/// SHA-256 in its metadata (an ETag is not a hash for every service). Checksums are sent only when the service needs
/// them: several compatible services refuse the SDK's default ones.
/// </summary>
internal sealed class S3Provider : IStorageProvider
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _prefix;

    public S3Provider(StorageLocation location, StorageKeys keys)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(keys);
        var config = new AmazonS3Config
        {
            ForcePathStyle = location.PathStyle,
            AuthenticationRegion = string.IsNullOrWhiteSpace(location.Region) ? "us-east-1" : location.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(60),
            MaxErrorRetry = 2,
        };
        if (!string.IsNullOrWhiteSpace(location.Endpoint))
        {
            config.ServiceURL = location.Endpoint;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(config.AuthenticationRegion);
        }

        _client = new AmazonS3Client(new BasicAWSCredentials(keys.AccessKey, keys.SecretKey), config);
        _bucket = location.Bucket ?? string.Empty;
        var prefix = (location.Prefix ?? string.Empty).Trim('/');
        _prefix = prefix.Length == 0 ? string.Empty : prefix + "/";
    }

    private string Key(string kind, string name) => $"{_prefix}{kind}/{name}";

    public async Task PutAsync(string kind, string name, Stream content, string sha256, CancellationToken cancellationToken)
    {
        // Not chunk-signed: several S3-compatible services do not take the streaming signature.
        var request = new PutObjectRequest { BucketName = _bucket, Key = Key(kind, name), InputStream = content, AutoCloseStream = false, UseChunkEncoding = false };
        if (sha256.Length > 0)
        {
            request.Metadata["sha256"] = sha256;
        }

        await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> OpenAsync(string kind, string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectAsync(_bucket, Key(kind, name), cancellationToken).ConfigureAwait(false);

            // Read whole (files here are small): the response is disposed and the caller gets a plain stream.
            using (response)
            {
                var copy = new MemoryStream();
                await response.ResponseStream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                copy.Position = 0;
                return copy;
            }
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string kind, string name, CancellationToken cancellationToken) =>
        _client.DeleteObjectAsync(_bucket, Key(kind, name), cancellationToken);

    public async Task<IReadOnlyList<StoredFile>> ListAsync(string kind, CancellationToken cancellationToken)
    {
        var files = new List<StoredFile>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = $"{_prefix}{kind}/" };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var o in response.S3Objects ?? [])
            {
                var name = o.Key[request.Prefix.Length..];
                if (FileKinds.IsName(name))
                {
                    files.Add(new StoredFile(kind, name, o.Size ?? 0, o.LastModified is { } at ? new DateTimeOffset(at.ToUniversalTime()) : DateTimeOffset.MinValue));
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return files;
    }

    public async Task<string?> TestAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_bucket))
        {
            return "Say which bucket.";
        }

        try
        {
            var name = "probe-" + Guid.NewGuid().ToString("N")[..12];
            using var probe = new MemoryStream("PC Diagnostic"u8.ToArray());
            await PutAsync(FileKinds.Backups, name, probe, string.Empty, cancellationToken).ConfigureAwait(false);
            await using (var read = await OpenAsync(FileKinds.Backups, name, cancellationToken).ConfigureAwait(false))
            {
                if (read is null)
                {
                    return "A file written there could not be read back.";
                }
            }

            await DeleteAsync(FileKinds.Backups, name, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (AmazonS3Exception ex)
        {
            return ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => "The keys are not allowed to write there (or the clock is wrong).",
                System.Net.HttpStatusCode.NotFound => "There is no such bucket.",
                _ => ex.ErrorCode is { Length: > 0 } code ? $"{code}: {ex.Message}" : ex.Message,
            };
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return "The service could not be reached: " + ex.Message;
        }
        catch (FormatException ex)
        {
            return "The service's answer could not be read (is it S3-compatible?): " + ex.Message;
        }
    }

    public void Dispose() => _client.Dispose();
}
