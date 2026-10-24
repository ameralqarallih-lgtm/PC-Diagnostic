using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PcDiag.Platform.Windows.Processes;

/// <summary>
/// Per-process details that need a process handle. Uses PROCESS_QUERY_LIMITED_INFORMATION, which a standard user
/// gets for most processes. Protected and some system processes still refuse; the result is then null.
/// </summary>
public static partial class ProcessDetails
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int ProcessCommandLineInformation = 60;
    private const int TokenUser = 1;

    /// <summary>Full executable path (QueryFullProcessImageName).</summary>
    public static unsafe string? TryGetImagePath(int pid)
    {
        using var process = Open(pid);
        if (process is null)
        {
            return null;
        }

        var buffer = stackalloc char[1024];
        var size = 1024;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    /// <summary>
    /// Command line through NtQueryInformationProcess(ProcessCommandLineInformation, Windows 8.1+), which works with
    /// PROCESS_QUERY_LIMITED_INFORMATION. Only call when the user enabled command-line capture.
    /// </summary>
    public static unsafe string? TryGetCommandLine(int pid)
    {
        using var process = Open(pid);
        if (process is null)
        {
            return null;
        }

        var buffer = new byte[8192];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            fixed (byte* data = buffer)
            {
                var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, data, buffer.Length, out var needed);
                if (status == unchecked((int)0xC0000004) && needed > buffer.Length && needed < 1024 * 1024)
                {
                    buffer = new byte[needed];
                    continue;
                }

                if (status < 0)
                {
                    return null;
                }

                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } followed by the characters.
                var length = *(ushort*)data;
                var chars = *(char**)(data + 8);
                return chars == null ? null : new string(chars, 0, length / 2);
            }
        }

        return null;
    }

    /// <summary>"DOMAIN\user" of the process token. Only call when the user enabled user-name capture.</summary>
    public static unsafe string? TryGetUser(int pid)
    {
        using var process = Open(pid);
        if (process is null || !OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        using (token)
        {
            var buffer = new byte[256];
            fixed (byte* data = buffer)
            {
                if (!GetTokenInformation(token, TokenUser, data, buffer.Length, out _))
                {
                    return null;
                }

                // TOKEN_USER { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                var sid = new SecurityIdentifier(*(nint*)data);
                try
                {
                    return sid.Translate(typeof(NTAccount)).Value;
                }
                catch (IdentityNotMappedException)
                {
                    return sid.Value;
                }
            }
        }
    }

    private static SafeProcessHandle? Open(int pid)
    {
        if (pid <= 4)
        {
            return null; // Idle and System have no user-mode image
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref int size);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, void* information, int length, out int returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, void* information, int length, out int returnLength);
}

/// <summary>
/// Removes values that look like secrets from command lines before they are stored:
/// <c>--password x</c>, <c>/pwd:x</c>, <c>token=x</c>, <c>apikey=x</c>, connection-string passwords and URL credentials.
/// Best effort; the Privacy setting that enables capture explains that command lines can still contain sensitive data.
/// </summary>
public static partial class CommandLineRedactor
{
    private const string Redacted = "[redacted]";

    /// <summary>Redacts known secret patterns.</summary>
    public static string Redact(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var result = SecretFlag().Replace(commandLine, m => m.Groups["key"].Value + m.Groups["sep"].Value + Redacted);
        result = UrlCredentials().Replace(result, m => m.Groups["scheme"].Value + Redacted + "@");
        return result;
    }

    // --password value, --password=value, /pwd:value, token=value, "Password=value;" (connection strings)
    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<key>(?:--?|/)?(?:password|passwd|pwd|pass|secret|token|apikey|api[-_]key|access[-_]key|client[-_]secret|auth)\b)(?<sep>\s*[=:]\s*|\s+)(?<value>""[^""]*""|'[^']*'|[^\s;&""']+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SecretFlag();

    // scheme://user:password@host
    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<scheme>[a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UrlCredentials();
}
