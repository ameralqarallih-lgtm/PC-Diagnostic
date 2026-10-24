using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using PcDiag.Platform.Windows.Processes;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>
/// Verifies the partly undocumented SYSTEM_PROCESS_INFORMATION offsets against documented sources, so a Windows change
/// that moved a field would fail here instead of silently recording wrong data.
/// Runs alone: it compares this process's own handle count and CPU time at two moments, which other tests running
/// in parallel in the same process (e.g. opening event log handles) would disturb.
/// </summary>
[Collection(OwnProcessMeasurements.Name)]
public partial class NtProcessesLayoutTests
{
    [Fact]
    public void Own_process_matches_documented_sources()
    {
        using var self = Process.GetCurrentProcess();
        Snapshot? found = null;
        var state = (Pid: self.Id, Result: (Snapshot?)null);

        // Burn a little CPU and do some I/O so the counters are non-trivial.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 200)
        {
        }

        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "pcdiag-io-probe.bin"), new byte[4 * 1024 * 1024]);

        new NtProcesses().Visit(ref state, static (ref (int Pid, Snapshot? Result) s, in RawProcess p) =>
        {
            if (p.Pid == s.Pid)
            {
                s.Result = new Snapshot(p.ParentPid, p.CreateTime, p.UserTime + p.KernelTime, p.Threads, p.Handles, p.WorkingSet, p.ReadTransfer, p.WriteTransfer, p.Name.ToString());
            }
        }).ShouldBeTrue();
        found = state.Result;
        self.Refresh();

        found.ShouldNotBeNull();
        found.Name.ShouldBe(Path.GetFileName(self.MainModule!.FileName), StringCompareShould.IgnoreCase);

        // Create time: exact match with Process.StartTime (both from the kernel's process create time).
        DateTime.FromFileTimeUtc(found.CreateTime).ShouldBe(self.StartTime.ToUniversalTime());

        // CPU time: our snapshot was taken just before Process read it, so it can only be slightly lower.
        var cpu = TimeSpan.FromTicks(found.CpuTime);
        cpu.ShouldBeLessThanOrEqualTo(self.TotalProcessorTime + TimeSpan.FromMilliseconds(50));
        cpu.ShouldBeGreaterThan(self.TotalProcessorTime - TimeSpan.FromMilliseconds(500));

        // Parent PID: WMI Win32_Process.ParentProcessId is the documented source.
        using var searcher = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {self.Id}");
        var parent = searcher.Get().Cast<ManagementObject>().Single();
        found.ParentPid.ShouldBe(Convert.ToInt32(parent["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture));

        // I/O transfer counts: documented GetProcessIoCounters.
        GetProcessIoCounters(self.Handle, out var io).ShouldBeTrue();
        found.WriteTransfer.ShouldBeGreaterThan(4L * 1024 * 1024 - 1);
        found.WriteTransfer.ShouldBeLessThanOrEqualTo((long)io.WriteTransferCount);
        found.ReadTransfer.ShouldBeLessThanOrEqualTo((long)io.ReadTransferCount);

        // Threads, handles, working set: close to Process (they change between the two reads).
        found.Threads.ShouldBeInRange(self.Threads.Count - 5, self.Threads.Count + 5);
        found.Handles.ShouldBeInRange(self.HandleCount - 100, self.HandleCount + 100);
        found.WorkingSet.ShouldBeInRange(self.WorkingSet64 / 2, self.WorkingSet64 * 2);
    }

    [Fact]
    public void Snapshot_contains_all_processes_with_distinct_identities()
    {
        var state = new List<(int Pid, long Create)>();
        new NtProcesses().Visit(ref state, static (ref List<(int, long)> list, in RawProcess p) => list.Add((p.Pid, p.CreateTime))).ShouldBeTrue();

        state.Count.ShouldBeGreaterThan(20);
        state.Select(p => p.Pid).Distinct().Count().ShouldBe(state.Count);
        state.ShouldContain(p => p.Pid == Environment.ProcessId);

        // Process.GetProcesses is a separate snapshot; the overlap should be nearly complete.
        var managed = Process.GetProcesses().Select(p => p.Id).ToHashSet();
        state.Count(p => managed.Contains(p.Pid)).ShouldBeGreaterThan((int)(managed.Count * 0.9));
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(nint process, out IoCounters counters);

    private sealed record Snapshot(int ParentPid, long CreateTime, long CpuTime, int Threads, int Handles, long WorkingSet, long ReadTransfer, long WriteTransfer, string Name);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}

/// <summary>Tests that measure the test process itself and must not run in parallel with any other test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OwnProcessMeasurements
{
    /// <summary>Collection name.</summary>
    public const string Name = "Own process measurements";
}
