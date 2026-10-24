using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Overlay;
using PcDiag.Engine.Overlay;

namespace PcDiag.Engine.Tests;

public sealed class OverlayKnownSourcesTests
{
    [Fact]
    public void A_value_not_read_yet_reads_its_name_as_last_seen_while_it_loads_then_none()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(folder.FullName, "overlay-sources.json");
            var time = new FakeTimeProvider();
            var first = new OverlayKnownSources(path, time);
            OverlaySource vram = new("gpu.k.vram", "VRAM", "RTX 4090", "B", 3e9, Max: 24e9);
            first.Complete([vram], ["gpu.k.vram"]).ShouldBe([vram]);
            time.Advance(TimeSpan.FromSeconds(31));
            first.Complete([vram], []);
            File.Exists(path).ShouldBeTrue();

            // Started again: the sensors are not read yet.
            var again = new OverlayKnownSources(path, time);
            var loading = again.Complete([], ["gpu.k.vram", "sensor.lhm/gpu-nvidia/0/temperature/2"]);
            loading[0].ShouldBe(new OverlaySource("gpu.k.vram", "VRAM", "RTX 4090", "B", null, Max: 24e9, Loading: true));
            loading[1].Label.ShouldBe("GPU temperature");
            loading[1].Loading.ShouldBeTrue();

            // Still missing after a while: no longer loading, still named.
            time.Advance(OverlayKnownSources.LoadingTime);
            again.Complete([], ["gpu.k.vram"]).ShouldHaveSingleItem().ShouldBe(new OverlaySource("gpu.k.vram", "VRAM", "RTX 4090", "B", null, Max: 24e9));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("sensor.lhm/gpu-nvidia/0/temperature/2", "GPU temperature")]
    [InlineData("sensor.lhm/lpc/nct6701d/0/voltage/8", "Board voltage")]
    [InlineData("gpu.gpu-10de-2684-467519da.vram", "VRAM")]
    [InlineData("cpu.temp", "CPU temp")]
    [InlineData("net.n1.down", "Download")]
    [InlineData("game.frametime", "Frame time")]
    public void A_value_never_seen_is_named_from_its_id(string id, string label) => OverlayStyles.LabelOf(id).ShouldBe(label);
}
