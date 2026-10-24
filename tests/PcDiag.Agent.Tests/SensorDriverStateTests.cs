using PcDiag.Agent.Screens;
using PcDiag.Screens;

namespace PcDiag.Agent.Tests;

public sealed class SensorDriverStateTests
{
    [Fact]
    public async Task A_failing_driver_shows_its_values_unavailable_and_is_set_aside_after_five_failures()
    {
        var package = ScreenDriverPackage.Parse("""
            { "id": "probe", "name": "Probe", "version": "1", "publisher": "x",
              "sensors": { "family": "http-json", "url": "http://127.0.0.1/", "every": 1, "sensors": [ { "id": "t", "name": "T", "path": "t" } ] } }
            """, out _)!;
        var driver = new Driver();
        using var state = new SensorDriverState(package, driver, _ => { });
        var now = DateTime.UtcNow;

        async Task Step()
        {
            state.Poll(now, CancellationToken.None);
            await Task.Delay(50);
            state.Poll(now, CancellationToken.None);
            now = now.AddSeconds(1.1);
        }

        await Step();
        state.Latest.Values.Single().Value.ShouldBe(20);
        state.Latest.Problem.ShouldBeNull();

        driver.Fail = true;
        await Step();
        state.Latest.Values.Single().Value.ShouldBeNull();
        state.Latest.Problem.ShouldNotBeNull().ShouldStartWith("Not read: the probe is unplugged");

        for (var i = 0; i < 4; i++)
        {
            await Step();
        }

        state.SetAside.ShouldBeTrue();
        state.Latest.Problem.ShouldNotBeNull().ShouldStartWith("Set aside after 5 failures");
        var reads = driver.Reads;
        await Step();
        driver.Reads.ShouldBe(reads);
    }

    private sealed class Driver : ISensorDriver
    {
        public bool Fail { get; set; }

        public int Reads { get; private set; }

        public string Id => "probe";

        public IReadOnlyList<SensorValue> Read(CancellationToken cancellationToken)
        {
            Reads++;
            return Fail ? throw new IOException("the probe is unplugged.") : [new SensorValue("probe", "t", "T", "other", string.Empty, 20)];
        }

        public void Dispose()
        {
        }
    }
}
