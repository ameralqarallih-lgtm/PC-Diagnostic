using System.Security.Principal;
using PcDiag.Core.Abstractions;

namespace PcDiag.Agent;

/// <summary>Detects whether the Agent runs with an elevated token.</summary>
internal static class ElevationDetector
{
    /// <summary>Checks the current process token. Under UAC, a filtered admin token reports false.</summary>
    public static ElevationInfo Detect()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return new ElevationInfo(principal.IsInRole(WindowsBuiltInRole.Administrator));
    }
}
