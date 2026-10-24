using PcDiag.Core.Control;

namespace PcDiag.Engine.Control;

/// <summary>
/// What may be ended or stopped. Refused: what Windows itself, its security or the recorder needs (ending it stops
/// Windows, logs the user off or blinds the recorder). Allowed with a confirmation: other parts of Windows (they
/// usually come back, but things may misbehave until a restart). Everything else is the user's own program.
/// </summary>
public static class ControlPolicy
{
    /// <summary>Processes that are never ended, and why.</summary>
    private static readonly Dictionary<string, string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "The Windows kernel.",
        ["System Idle Process"] = "Not a real process: idle CPU time.",
        ["Idle"] = "Not a real process: idle CPU time.",
        ["Registry"] = "Holds the Windows registry.",
        ["Secure System"] = "Windows' isolated security core.",
        ["Memory Compression"] = "Windows' compressed memory.",
        ["smss.exe"] = "Starts Windows sessions; ending it stops Windows.",
        ["csrss.exe"] = "Ending it stops Windows with a blue screen.",
        ["wininit.exe"] = "Ending it stops Windows with a blue screen.",
        ["winlogon.exe"] = "Handles sign-in; ending it signs you out.",
        ["services.exe"] = "Runs every Windows service; ending it stops Windows.",
        ["lsass.exe"] = "Handles security and passwords; ending it restarts Windows.",
        ["LsaIso.exe"] = "Windows' isolated credential guard.",
        ["svchost.exe"] = "Hosts Windows services: stop the service you mean instead (Services).",
        ["dwm.exe"] = "Draws the screen; the display would go black.",
        ["fontdrvhost.exe"] = "Draws fonts for Windows.",
        ["MsMpEng.exe"] = "Microsoft Defender antivirus.",
        ["MpDefenderCoreService.exe"] = "Microsoft Defender antivirus.",
        ["NisSrv.exe"] = "Microsoft Defender network protection.",
        ["SecurityHealthService.exe"] = "Windows Security.",
        ["SgrmBroker.exe"] = "Windows' system guard.",
        ["PcDiag.Agent.exe"] = "The recorder itself.",
        ["PcDiag.Web.exe"] = "This dashboard's host.",
    };

    /// <summary>Parts of Windows that may be ended after confirming, and what happens.</summary>
    private static readonly Dictionary<string, string> CautionProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer.exe"] = "The taskbar, desktop and File Explorer windows close and come back.",
        ["sihost.exe"] = "Windows' shell experience host; the desktop may misbehave until you sign in again.",
        ["ctfmon.exe"] = "Text input (keyboard layouts, touch keyboard).",
        ["audiodg.exe"] = "Sound stops for a moment.",
        ["spoolsv.exe"] = "Printing stops until the print spooler service starts again.",
        ["WmiPrvSE.exe"] = "System information for programs; Windows starts it again when needed.",
        ["RuntimeBroker.exe"] = "Permissions for Store apps; those apps may misbehave.",
        ["dllhost.exe"] = "Runs Windows components for programs.",
        ["conhost.exe"] = "The window of a command-line program; that program ends too.",
        ["taskhostw.exe"] = "Runs Windows tasks.",
        ["SearchHost.exe"] = "Windows search; it starts again when you search.",
        ["StartMenuExperienceHost.exe"] = "The Start menu; it starts again by itself.",
        ["ShellExperienceHost.exe"] = "Notifications and the clock flyout; they come back by themselves.",
        ["TextInputHost.exe"] = "The touch keyboard and emoji panel.",
        ["SearchIndexer.exe"] = "Indexing for search; it starts again by itself.",
    };

    /// <summary>Services that are never stopped or changed, and why.</summary>
    private static readonly Dictionary<string, string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RpcSs"] = "Windows' core communication; nothing works without it.",
        ["RpcEptMapper"] = "Windows' core communication.",
        ["DcomLaunch"] = "Starts Windows components; nothing works without it.",
        ["LSM"] = "Manages sign-in sessions.",
        ["SamSs"] = "User accounts and security.",
        ["EventLog"] = "The Windows event log (the recorder reads it).",
        ["PlugPlay"] = "Detects devices.",
        ["Power"] = "Power management.",
        ["ProfSvc"] = "Loads user profiles.",
        ["BrokerInfrastructure"] = "Runs background tasks for Windows.",
        ["CoreMessagingRegistrar"] = "Windows' internal messaging.",
        ["SystemEventsBroker"] = "Windows' system events.",
        ["StateRepository"] = "Store apps and the Start menu need it.",
        ["gpsvc"] = "Group Policy.",
        ["UserManager"] = "User sessions.",
        ["Winmgmt"] = "System information (WMI); the recorder needs it.",
        ["BFE"] = "The firewall's base.",
        ["mpssvc"] = "Windows Firewall.",
        ["WinDefend"] = "Microsoft Defender antivirus.",
        ["WdNisSvc"] = "Microsoft Defender network protection.",
        ["SecurityHealthService"] = "Windows Security.",
        ["Sense"] = "Microsoft Defender for Endpoint.",
        ["wscsvc"] = "Security Center.",
        ["CryptSvc"] = "Certificates and signatures.",
        ["KeyIso"] = "Protects keys and passwords.",
        ["Schedule"] = "The Task Scheduler (it starts the recorder).",
        ["Dhcp"] = "Gets the network address; the network would drop.",
        ["Dnscache"] = "Resolves names on the network.",
        ["nsi"] = "Network information for Windows.",
        ["AudioEndpointBuilder"] = "Sound devices.",
        ["TimeBrokerSvc"] = "Windows' scheduled background work.",
    };

    /// <summary>A process's protection: refused, confirm, or none.</summary>
    /// <param name="name">Image name.</param>
    /// <param name="pid">Process ID (0 and 4 are Windows itself).</param>
    /// <param name="windows">The program is part of Windows (under the Windows folder).</param>
    /// <param name="critical">Windows marked it critical.</param>
    /// <param name="self">The recorder's own process.</param>
    public static (ControlProtection Protection, string? Reason) ForProcess(string name, int pid, bool windows, bool critical, bool self)
    {
        if (self)
        {
            return (ControlProtection.Protected, "The recorder itself.");
        }

        if (pid is 0 or 4)
        {
            return (ControlProtection.Protected, "Windows itself.");
        }

        if (critical)
        {
            return (ControlProtection.Protected, "Windows marked it critical: ending it stops Windows with a blue screen.");
        }

        if (ProtectedProcesses.TryGetValue(name, out var reason))
        {
            return (ControlProtection.Protected, reason);
        }

        if (CautionProcesses.TryGetValue(name, out var caution))
        {
            return (ControlProtection.Caution, caution);
        }

        return windows ? (ControlProtection.Caution, "Part of Windows: Windows may misbehave until it is restarted.") : (ControlProtection.None, null);
    }

    /// <summary>A service's protection.</summary>
    public static (ControlProtection Protection, string? Reason) ForService(string name, bool windows) =>
        ProtectedServices.TryGetValue(name, out var reason) ? (ControlProtection.Protected, reason)
        : windows ? (ControlProtection.Caution, "A Windows service: features that use it stop working until it starts again.")
        : (ControlProtection.None, null);

    /// <summary>Whether a program path is part of Windows (under the Windows folder).</summary>
    public static bool IsWindowsPath(string? path, string windowsFolder) =>
        path is { Length: > 0 } && path.StartsWith(windowsFolder.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
}
