namespace PcDiag.Core.Telemetry;

/// <summary>
/// Identifies one process instance. PIDs are reused by Windows, so the process creation time is part of the identity.
/// </summary>
/// <param name="Pid">Process ID.</param>
/// <param name="CreateTimeUtcTicks">Process creation time as UTC <see cref="DateTime.Ticks"/>, or 0 when unknown.</param>
public readonly record struct ProcessKey(int Pid, long CreateTimeUtcTicks)
{
    /// <summary>
    /// A stable 64-bit identifier for storage (FNV-1a over PID and creation time).
    /// Stable across runs and machines, unlike <see cref="object.GetHashCode"/>.
    /// </summary>
    public long ToStorageId()
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        hash = Mix(hash, unchecked((ulong)Pid), prime);
        hash = Mix(hash, unchecked((ulong)CreateTimeUtcTicks), prime);
        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value, ulong prime)
        {
            for (var i = 0; i < 8; i++)
            {
                hash ^= (value >> (i * 8)) & 0xFF;
                hash *= prime;
            }

            return hash;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Pid}@{CreateTimeUtcTicks}";
}
