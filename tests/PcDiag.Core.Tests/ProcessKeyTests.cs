using PcDiag.Core.Telemetry;

namespace PcDiag.Core.Tests;

public class ProcessKeyTests
{
    [Fact]
    public void Storage_id_is_stable()
    {
        // A fixed expected value guards against accidental algorithm changes, which would orphan stored identities.
        var key = new ProcessKey(1234, 638_000_000_000_000_000);

        // Expected value computed independently (FNV-1a 64 over the little-endian bytes of PID then ticks).
        key.ToStorageId().ShouldBe(-6957767537088704091L);
        new ProcessKey(1234, 638_000_000_000_000_000).ToStorageId().ShouldBe(key.ToStorageId());
    }

    [Fact]
    public void Reused_pid_with_different_start_time_gets_different_id()
    {
        var first = new ProcessKey(4242, 638_000_000_000_000_000);
        var reused = new ProcessKey(4242, 638_000_000_100_000_000);

        reused.ToStorageId().ShouldNotBe(first.ToStorageId());
    }

    [Fact]
    public void Ids_do_not_collide_for_many_processes()
    {
        var ids = new HashSet<long>();
        for (var pid = 4; pid < 200_000; pid += 4)
        {
            ids.Add(new ProcessKey(pid, 638_000_000_000_000_000 + pid).ToStorageId()).ShouldBeTrue();
        }
    }
}
