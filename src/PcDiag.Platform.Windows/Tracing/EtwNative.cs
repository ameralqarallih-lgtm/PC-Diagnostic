using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcDiag.Platform.Windows.Tracing;

/// <summary>The ETW real-time session API (advapi32), shared by the recorder's sessions.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class EtwNative
{
    /// <summary>
    /// Starts a real-time session. Its buffers are handed to the consumer at least every <paramref name="flushMs"/>
    /// (a second by default; less for a reading shown live, such as the frame rate).
    /// </summary>
    internal static uint StartSession(string name, out ulong session, uint flushMs = 1000)
    {
        var buffer = Properties(out var props);
        try
        {
            props->LogFileMode = EventTraceRealTimeMode | (flushMs < 1000 ? EventTraceUseMsFlushTimer : 0);
            props->BufferSize = 64;
            props->MinimumBuffers = 4;
            props->MaximumBuffers = 32;
            props->FlushTimer = flushMs < 1000 ? flushMs : flushMs / 1000;
            fixed (ulong* handle = &session)
            {
                return StartTraceW(handle, name, props);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void StopSession(string name)
    {
        var buffer = Properties(out var props);
        try
        {
            _ = ControlTraceW(0, name, props, EventTraceControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>EVENT_TRACE_PROPERTIES followed by room for the session name.</summary>
    internal static nint Properties(out EventTraceProperties* props)
    {
        var size = sizeof(EventTraceProperties) + 1024;
        var buffer = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)buffer, size).Clear();
        props = (EventTraceProperties*)buffer;
        props->Wnode.BufferSize = (uint)size;
        props->Wnode.Flags = WnodeFlagTracedGuid;
        props->Wnode.ClientContext = 1; // QPC timestamps
        props->LoggerNameOffset = (uint)sizeof(EventTraceProperties);
        return buffer;
    }

    internal const uint ErrorAlreadyExists = 183;
    internal const uint EnableProvider = 1;
    internal const byte TraceLevelInformation = 4;
    internal const uint EventTraceRealTimeMode = 0x100;
    internal const uint EventTraceUseMsFlushTimer = 0x10;
    internal const uint EventTraceControlStop = 1;
    internal const uint WnodeFlagTracedGuid = 0x00020000;
    internal const uint ProcessTraceModeRealTime = 0x100;
    internal const uint ProcessTraceModeEventRecord = 0x10000000;

    /// <summary>
    /// Events keep the session's clock (QPC, see <see cref="Properties"/>); without it ETW hands them over in system
    /// time (FILETIME), which cannot be compared with <see cref="System.Diagnostics.Stopwatch"/>.
    /// </summary>
    internal const uint ProcessTraceModeRawTimestamp = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_LOGFILEW (448 bytes on x64); only the fields used here are named.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    internal struct EventTraceLogfile
    {
        [FieldOffset(8)]
        public nint LoggerName;

        [FieldOffset(28)]
        public uint ProcessTraceMode;

        [FieldOffset(424)]
        public nint EventRecordCallback;

        [FieldOffset(440)]
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventHeader
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventRecord
    {
        public EventHeader EventHeader;
        public uint BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public nint ExtendedData;
        public nint UserData;
        public nint UserContext;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint StartTraceW(ulong* sessionHandle, string sessionName, EventTraceProperties* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ControlTraceW(ulong sessionHandle, string sessionName, EventTraceProperties* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    internal static partial uint EnableTraceEx2(ulong sessionHandle, Guid* providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, void* enableParameters);

    /// <summary>
    /// Enables a provider for only some of its events (an event id filter, applied before the events reach the
    /// session's buffers).
    /// </summary>
    internal static uint EnableEvents(ulong session, Guid provider, ulong keywords, ReadOnlySpan<ushort> ids)
    {
        // EVENT_FILTER_EVENT_ID: FilterIn, Reserved, Count, then the ids.
        var size = 4 + (2 * ids.Length);
        var filter = stackalloc byte[size];
        filter[0] = 1;
        filter[1] = 0;
        *(ushort*)(filter + 2) = (ushort)ids.Length;
        for (var i = 0; i < ids.Length; i++)
        {
            *(ushort*)(filter + 4 + (2 * i)) = ids[i];
        }

        var descriptor = new EventFilterDescriptor { Ptr = (ulong)filter, Size = (uint)size, Type = EventFilterTypeEventId };
        var parameters = new EnableTraceParameters { Version = 2, EnableFilterDesc = &descriptor, FilterDescCount = 1 };
        return EnableTraceEx2(session, &provider, EnableProvider, TraceLevelInformation, keywords, 0, 0, &parameters);
    }

    internal const uint EventFilterTypeEventId = 0x80000200;

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventFilterDescriptor
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EnableTraceParameters
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public Guid SourceId;
        public EventFilterDescriptor* EnableFilterDesc;
        public uint FilterDescCount;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW")]
    internal static partial ulong OpenTraceW(EventTraceLogfile* logfile);

    [LibraryImport("advapi32.dll")]
    internal static partial uint ProcessTrace(ulong* handleArray, uint handleCount, void* startTime, void* endTime);

    [LibraryImport("advapi32.dll")]
    internal static partial uint CloseTrace(ulong traceHandle);
}
