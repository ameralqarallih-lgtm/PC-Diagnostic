using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PcDiag.Engine.Events;

/// <summary>One payload value of an event: named (manifest events, UserData) or positional (classic events).</summary>
/// <param name="Name">The Data element's Name attribute or the UserData element name; null for unnamed classic data.</param>
/// <param name="Value">The value as rendered by the Event Log.</param>
public readonly record struct EventDataItem(string? Name, string Value);

/// <summary>
/// The parts of a Windows event record that classification needs, parsed from the record's XML
/// (<c>EventRecord.ToXml()</c>). Live events and replayed test fixtures go through the same parser.
/// </summary>
/// <param name="Channel">Channel (log) name, e.g. "System".</param>
/// <param name="RecordId">Event record ID (unique per channel).</param>
/// <param name="Provider">Provider name (the Provider element's Name attribute).</param>
/// <param name="EventId">Event ID (without qualifiers).</param>
/// <param name="Level">Level (1 critical, 2 error, 3 warning, 4 information; 0 for some classic events).</param>
/// <param name="Timestamp">When the event was recorded (UTC).</param>
/// <param name="ExecutionProcessId">The process that logged the event (System/Execution/@ProcessID).</param>
/// <param name="Data">EventData values in order, followed by UserData leaf values.</param>
/// <param name="BinaryHex">EventData/Binary as hex, when present.</param>
public sealed record EventRecordData(
    string Channel,
    long? RecordId,
    string Provider,
    int EventId,
    int Level,
    DateTimeOffset Timestamp,
    int? ExecutionProcessId,
    IReadOnlyList<EventDataItem> Data,
    string? BinaryHex)
{
    /// <summary>The first value with this name (case-insensitive), or null.</summary>
    public string? Get(string name)
    {
        foreach (var item in Data)
        {
            if (item.Name is not null && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value;
            }
        }

        return null;
    }

    /// <summary>The value at a position among EventData values, or null.</summary>
    public string? At(int index) => index >= 0 && index < Data.Count ? Data[index].Value : null;

    /// <summary>
    /// <see cref="BinaryHex"/> decoded as UTF-16LE text (the Service Control Manager stores the service key name
    /// there). Null when absent or not printable text.
    /// </summary>
    public string? BinaryText()
    {
        if (string.IsNullOrEmpty(BinaryHex) || BinaryHex.Length % 4 != 0 || BinaryHex.Length > 2048)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(BinaryHex);
        }
        catch (FormatException)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        return text.Length > 0 && text.All(c => !char.IsControl(c)) ? text : null;
    }
}

/// <summary>Parses event XML as rendered by the Windows Event Log.</summary>
public static class EventXml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = 1_000_000,
    };

    /// <summary>Parses one event. Returns null when the XML is not a well-formed event.</summary>
    public static EventRecordData? Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = document.Root;
        var system = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
        if (root is null || root.Name.LocalName != "Event" || system is null)
        {
            return null;
        }

        var provider = Child(system, "Provider")?.Attribute("Name")?.Value;
        var channel = Child(system, "Channel")?.Value;
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(channel)
            || !int.TryParse(Child(system, "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            return null;
        }

        _ = int.TryParse(Child(system, "Level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level);
        long? recordId = long.TryParse(Child(system, "EventRecordID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;
        int? executionPid = int.TryParse(Child(system, "Execution")?.Attribute("ProcessID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : null;
        var timestamp = DateTimeOffset.TryParse(Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : DateTimeOffset.UnixEpoch;

        var data = new List<EventDataItem>();
        string? binary = null;
        if (root.Elements().FirstOrDefault(e => e.Name.LocalName == "EventData") is { } eventData)
        {
            foreach (var element in eventData.Elements())
            {
                if (element.Name.LocalName == "Data")
                {
                    data.Add(new EventDataItem(element.Attribute("Name")?.Value, element.Value));
                }
                else if (element.Name.LocalName == "Binary")
                {
                    binary = element.Value;
                }
            }
        }

        if (root.Elements().FirstOrDefault(e => e.Name.LocalName == "UserData") is { } userData)
        {
            foreach (var leaf in userData.Descendants().Where(e => !e.HasElements))
            {
                data.Add(new EventDataItem(leaf.Name.LocalName, leaf.Value));
            }
        }

        return new EventRecordData(channel, recordId, provider, eventId, level, timestamp, executionPid, data, binary);
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
}
