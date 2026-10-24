using PcDiag.Platform.Windows.Display;
using Xunit.Abstractions;

namespace PcDiag.Collectors.IntegrationTests;

public sealed unsafe class DisplayConfigTests(ITestOutputHelper output)
{
    [Fact]
    public void Struct_sizes_match_wingdi_h()
    {
        // Sizes computed from wingdi.h (Windows SDK 10.0.26100).
        sizeof(DisplayConfig.PathSourceInfo).ShouldBe(20);
        sizeof(DisplayConfig.PathTargetInfo).ShouldBe(48);
        sizeof(DisplayConfig.PathInfo).ShouldBe(72);
        sizeof(DisplayConfig.ModeInfo).ShouldBe(64);
        sizeof(DisplayConfig.DeviceInfoHeader).ShouldBe(20);
        sizeof(DisplayConfig.TargetDeviceName).ShouldBe(420);
        sizeof(DisplayConfig.SourceDeviceName).ShouldBe(84);
        sizeof(DisplayConfig.AdapterName).ShouldBe(276);
        sizeof(DisplayConfig.AdvancedColorInfo).ShouldBe(32);
        sizeof(DisplayConfig.AdvancedColorInfo2).ShouldBe(36);
    }

    [Fact]
    public void The_monitors_are_read_in_physical_pixels_with_their_scaling_and_match_the_display_configuration()
    {
        var monitors = MonitorLayout.Read();
        output.WriteLine(string.Join(Environment.NewLine, monitors));
        if (monitors.Count == 0)
        {
            return; // no desktop (a service or a locked session)
        }

        monitors.Count(m => m.Primary).ShouldBe(1);
        monitors.ShouldAllBe(m => m.Width > 0 && m.Height > 0 && m.Scale >= 1 && m.DeviceName.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase));
        var primary = monitors.Single(m => m.Primary);
        (primary.X, primary.Y).ShouldBe((0, 0));

        // Each one is in the display configuration under the same device name (that is how its stable key is found).
        var paths = DisplayConfig.Query().Paths;
        if (paths.Count > 0)
        {
            monitors.ShouldAllBe(m => paths.Any(p => string.Equals(p.GdiDeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void Active_displays_are_read_and_edid_ids_match_the_device_path()
    {
        if (DesktopLocked(output))
        {
            return;
        }

        var result = DisplayConfig.Query();

        result.Error.ShouldBe(DisplayConfig.Success);
        result.Paths.ShouldNotBeEmpty();
        result.Paths.Count(p => p.IsPrimary).ShouldBe(1);
        foreach (var p in result.Paths)
        {
            output.WriteLine($"{p.GdiDeviceName} '{p.FriendlyName}' {p.Manufacturer}{p.ProductCode} conn#{p.ConnectorInstance} tech={p.OutputTechnology} rot={p.Rotation} " +
                $"{p.Width}x{p.Height}@{p.RefreshRateHz}Hz at ({p.PositionX},{p.PositionY}) primary={p.IsPrimary} adapter={p.Adapter} target={p.TargetId}");
            output.WriteLine($"    color={p.Color}  path={p.MonitorDevicePath}  adapter path={p.AdapterDevicePath}");

            p.Width.ShouldBeGreaterThan(0);
            p.RefreshRateHz.ShouldNotBeNull().ShouldBeInRange(20, 1000);

            // The monitor device path embeds the PnP ID: \\?\DISPLAY#DELA1A2#... = manufacturer + product code.
            if (p.Manufacturer is not null && p.MonitorDevicePath is not null)
            {
                p.MonitorDevicePath.ShouldContain($"#{p.Manufacturer}{p.ProductCode}#", Case.Insensitive);
            }
        }
    }

    /// <summary>
    /// Windows refuses display configuration queries (access denied) while the workstation is locked, so tests of the
    /// real configuration have nothing to check then. Any other error still fails them.
    /// </summary>
    internal static bool DesktopLocked(ITestOutputHelper output)
    {
        if (DisplayConfig.Query().Error != DisplayConfig.AccessDenied)
        {
            return false;
        }

        output.WriteLine("Display configuration is not readable (access denied): the workstation is locked or not in the interactive session; nothing to check.");
        return true;
    }

    [Theory]
    [InlineData("SwapEffectUpgradeEnable=0;AutoHDREnable=0;VRROptimizeEnable=1;", true)]
    [InlineData("VRROptimizeEnable=0;", false)]
    [InlineData("SwapEffectUpgradeEnable=1;", null)]
    [InlineData(null, null)]
    public void Variable_refresh_setting_is_parsed(string? settings, bool? expected) =>
        DisplayConfig.ParseVariableRefreshSetting(settings).ShouldBe(expected);
}
