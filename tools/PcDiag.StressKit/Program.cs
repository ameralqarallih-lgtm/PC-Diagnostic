using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PcDiag.StressKit;

/// <summary>
/// Makes real load and real failures so the recorder, its anomaly rules and incidents can be checked on a real PC:
/// <c>PcDiag.StressKit cpu|memory|disk|hang|crash [options]</c>. Every command stops by itself and cleans up after
/// itself; Ctrl+C stops it early. Start recording first (dashboard or <c>PcDiag.Agent.exe start</c>).
/// </summary>
internal static class Program
{
    private const string Usage = """
        PC Diagnostic StressKit: real load and failures for testing the recorder (start recording first).

          cpu    [--seconds 45] [--threads N]       Every core busy (rule cpu-high: above 95 % for 30 s).
          memory [--gigabytes 4] [--seconds 30]     Commits and touches memory, then frees it (memory-commit-high,
                                                    memory-low when little is left). Refuses to leave < 0.5 GB.
          disk   [--megabytes 2048] [--seconds 30] [--path DIR]
                                                    Write-through writes and random reads on a temporary file
                                                    (disk-latency-high, disk-queue-high on slower disks).
          hang   [--seconds 20]                     A window that stops responding (shown as "Not responding";
                                                    Windows logs Application Hang only if you close it while hung).
          crash                                     This process crashes with an unhandled exception (app-crash:
                                                    Windows logs Application Error 1000; creates an incident).
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 64 : 0;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            var options = Options.Parse(args[1..]);
            return args[0].ToLowerInvariant() switch
            {
                "cpu" => Cpu(options.Seconds(45), options.Int("threads", Environment.ProcessorCount), cancel.Token),
                "memory" => Memory(options.Double("gigabytes", 4), options.Seconds(30), cancel.Token),
                "disk" => Disk(options.Int("megabytes", 2048), options.Seconds(30), options.Text("path") ?? Path.GetTempPath(), cancel.Token),
                "hang" => Hang(options.Seconds(20)),
                "crash" => Crash(),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'."),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 64;
        }
    }

    private static int Cpu(TimeSpan duration, int threads, CancellationToken cancel)
    {
        threads = Math.Clamp(threads, 1, Environment.ProcessorCount);
        Console.WriteLine($"CPU: {threads} busy threads for {duration.TotalSeconds:F0} s. Expect a CPU saturation anomaly after 30 s.");
        var until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            var x = 1.0;
            while (Stopwatch.GetTimestamp() < until && !cancel.IsCancellationRequested)
            {
                for (var i = 0; i < 100_000; i++)
                {
                    x = Math.Sqrt(x + i);
                }
            }

            GC.KeepAlive(x);
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());
        Console.WriteLine("CPU: done.");
        return 0;
    }

    private static int Memory(double gigabytes, TimeSpan hold, CancellationToken cancel)
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx failed.");
        }

        var bytes = (long)(gigabytes * (1L << 30));
        var floor = 1L << 29; // 0.5 GB stays available
        if ((long)status.AvailablePhysical - bytes < floor)
        {
            throw new ArgumentException($"Refusing: {gigabytes:0.##} GB would leave less than 0.5 GB of the {status.AvailablePhysical / (double)(1L << 30):F1} GB available.");
        }

        Console.WriteLine($"Memory: committing and touching {gigabytes:0.##} GB (available now {status.AvailablePhysical / (double)(1L << 30):F1} GB), holding {hold.TotalSeconds:F0} s.");
        var chunks = new List<byte[]>();
        const int chunk = 64 << 20;
        for (long done = 0; done < bytes && !cancel.IsCancellationRequested; done += chunk)
        {
            var block = GC.AllocateUninitializedArray<byte>((int)Math.Min(chunk, bytes - done));
            for (var i = 0; i < block.Length; i += 4096)
            {
                block[i] = 1; // touch every page so it is really in use
            }

            chunks.Add(block);
        }

        cancel.WaitHandle.WaitOne(hold);
        chunks.Clear();
        GC.Collect();
        Console.WriteLine("Memory: released.");
        return 0;
    }

    private static int Disk(int megabytes, TimeSpan duration, string directory, CancellationToken cancel)
    {
        var path = Path.Combine(directory, $"pcdiag-stresskit-{Environment.ProcessId}.tmp");
        Console.WriteLine($"Disk: {megabytes} MB write-through file at {path}, random reads and writes for {duration.TotalSeconds:F0} s.");
        var until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            var block = new byte[1 << 20];
            Random.Shared.NextBytes(block);
            for (var i = 0; i < megabytes && !cancel.IsCancellationRequested && Stopwatch.GetTimestamp() < until; i++)
            {
                file.Write(block);
            }

            var small = new byte[64 << 10];
            var length = file.Length;
            while (Stopwatch.GetTimestamp() < until && !cancel.IsCancellationRequested && length > small.Length)
            {
                file.Position = Random.Shared.NextInt64(0, length - small.Length) & ~4095L;
                if (Random.Shared.Next(2) == 0)
                {
                    file.ReadExactly(small);
                }
                else
                {
                    file.Write(small);
                }
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Disk: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Disk: done; the file was deleted.");
        return 0;
    }

    private static int Hang(TimeSpan duration)
    {
        Console.WriteLine($"Hang: a window that stops responding for {duration.TotalSeconds:F0} s (Windows marks it Not responding after about 5 s).");
        Application.EnableVisualStyles();
        using var form = new Form { Text = "PC Diagnostic StressKit - hanging on purpose", Width = 520, Height = 160, StartPosition = FormStartPosition.CenterScreen };
        form.Controls.Add(new Label { Text = $"This window stops responding for {duration.TotalSeconds:F0} seconds, then closes.", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
        form.Shown += async (_, _) =>
        {
            await Task.Delay(500);
            Thread.Sleep(duration); // block the message loop: the window is hung
            form.Close();
        };
        Application.Run(form);
        Console.WriteLine("Hang: done.");
        return 0;
    }

    private static int Crash()
    {
        Console.WriteLine("Crash: throwing an unhandled exception on a background thread now.");
        var thread = new Thread(() => throw new InvalidOperationException("PC Diagnostic StressKit crashed on purpose."));
        thread.Start();
        thread.Join();
        return 1; // not reached
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed class Options(Dictionary<string, string> values)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Expected --name value, got '{args[i]}'.");
                }

                values[args[i][2..]] = args[i + 1];
            }

            return new Options(values);
        }

        public string? Text(string name) => values.GetValueOrDefault(name);

        public int Int(string name, int fallback) =>
            values.TryGetValue(name, out var text) ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

        public double Double(string name, double fallback) =>
            values.TryGetValue(name, out var text) ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;

        public TimeSpan Seconds(int fallback) => TimeSpan.FromSeconds(Math.Clamp(Int("seconds", fallback), 1, 3600));
    }
}
