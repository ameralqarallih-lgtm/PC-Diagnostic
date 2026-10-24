using PcDiag.Storage.Sqlite;

namespace PcDiag.Storage.Tests;

/// <summary>A unique temporary data directory, deleted on dispose.</summary>
internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        // Pooled connections hold file handles open. Only this folder's: clearing every pool would close connections
        // that tests running at the same time are taking.
        new SqliteConnectionFactory().ReleasePooledConnectionsUnder(Path);
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp folder is cleaned up eventually.
        }
    }
}
