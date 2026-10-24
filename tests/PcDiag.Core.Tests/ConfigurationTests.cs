using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;

namespace PcDiag.Core.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        new StorageOptionsValidator().Validate(null, new StorageOptions()).Succeeded.ShouldBeTrue();
        new RecorderOptionsValidator().Validate(null, new RecorderOptions()).Succeeded.ShouldBeTrue();
        new SamplingOptionsValidator().Validate(null, new SamplingOptions()).Succeeded.ShouldBeTrue();
        new EventOptionsValidator().Validate(null, new EventOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Default_recording_mode_is_manual_and_sensitive_capture_is_off()
    {
        new RecorderOptions().Mode.ShouldBe(RecordingMode.Manual);

        var privacy = new PrivacyOptions();
        privacy.CaptureCommandLines.ShouldBeFalse();
        privacy.CaptureUserNames.ShouldBeFalse();
        privacy.CaptureWifiNetworkNames.ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    public void Allowed_pre_incident_windows_are_accepted(int minutes)
    {
        new RecorderOptionsValidator().Validate(null, new RecorderOptions { PreIncidentMinutes = minutes }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(60)]
    public void Other_pre_incident_windows_are_rejected(int minutes)
    {
        var result = new RecorderOptionsValidator().Validate(null, new RecorderOptions { PreIncidentMinutes = minutes });

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain("PreIncidentMinutes");
    }

    [Fact]
    public void Scheduled_mode_requires_windows_with_days()
    {
        var validator = new RecorderOptionsValidator();

        validator.Validate(null, new RecorderOptions { Mode = RecordingMode.Scheduled }).Failed.ShouldBeTrue();

        var noDays = new RecorderOptions { Mode = RecordingMode.Scheduled, Schedule = [new RecordingWindow { Start = new(9, 0), End = new(17, 0) }] };
        validator.Validate(null, noDays).FailureMessage!.ShouldContain("at least one day");

        var ok = new RecorderOptions
        {
            Mode = RecordingMode.Scheduled,
            Schedule = [new RecordingWindow { Days = [DayOfWeek.Monday], Start = new(9, 0), End = new(17, 0) }],
        };
        validator.Validate(null, ok).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Out_of_range_values_report_every_problem()
    {
        var result = new SamplingOptionsValidator().Validate(null, new SamplingOptions
        {
            SystemIntervalMs = 10,
            ProcessIntervalMs = 100_000,
            CpuBudgetPercent = double.NaN,
        });

        result.Failures!.Count().ShouldBe(3);
    }

    [Fact]
    public void Event_collection_limits_are_bounded()
    {
        var result = new EventOptionsValidator().Validate(null, new EventOptions
        {
            BackfillMinutes = 2000,
            BootBackfillMaxHours = -1,
            MaxPerRulePerMinute = 0,
            MaxMessageLength = 50,
        });

        result.Failures!.Count().ShouldBe(4);
        new EventOptions().BackfillMinutes.ShouldBe(30);
    }

    [Fact]
    public void Retention_is_bounded()
    {
        new StorageOptionsValidator().Validate(null, new StorageOptions { TelemetryRetentionHours = 0 }).Failed.ShouldBeTrue();
        new StorageOptionsValidator().Validate(null, new StorageOptions { TelemetryRetentionHours = 73 }).Failed.ShouldBeTrue();
        new StorageOptionsValidator().Validate(null, new StorageOptions { WriteBehindSeconds = 61 }).Failed.ShouldBeTrue();
    }
}
