using Microsoft.Data.Sqlite;
using PcDiag.Core.History;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Reading;

/// <summary>
/// Inventory, drivers, display topology and sensor availability from a telemetry database (the live one for the
/// System page, or an incident's copy for what the PC looked like then).
/// </summary>
public sealed class SystemReader(SqliteConnectionFactory connectionFactory)
{
    /// <summary>Most sensors listed.</summary>
    public const int MaxSensors = 1000;

    private static readonly System.Text.Json.JsonSerializerOptions DeviceJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The overview of a database; empty when the file does not exist.</summary>
    public async Task<SystemOverview> OverviewAsync(string database, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return new SystemOverview(new Dictionary<string, string?>(), [], null, [], null, null, null, null, [], []);
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);

        var information = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        await foreach (var row in RowsAsync(connection, "SELECT key, value FROM SystemInformation;", cancellationToken).ConfigureAwait(false))
        {
            information[row.GetString(0)] = row.IsDBNull(1) ? null : row.GetString(1);
        }

        var devices = new List<DeviceInfo>();
        await foreach (var row in RowsAsync(connection, "SELECT device_id, kind, name, vendor, details_json, first_seen, last_seen, stable_key FROM Devices ORDER BY kind, name;", cancellationToken).ConfigureAwait(false))
        {
            devices.Add(new DeviceInfo(row.GetInt64(0), row.GetString(1), Text(row, 2), Text(row, 3), HistoryReader.ParseJson(Text(row, 4)), row.GetInt64(5), row.GetInt64(6), Text(row, 7)));
        }

        var driversTs = await ScalarAsync(connection, "SELECT MAX(snapshot_ts) FROM Drivers;", cancellationToken).ConfigureAwait(false);
        var drivers = new List<InstalledDriver>();
        if (driversTs is { } snapshot)
        {
            await foreach (var row in RowsAsync(connection,
                               "SELECT device_class, device_name, hardware_id, provider, version, driver_date, inf, signer FROM Drivers WHERE snapshot_ts = $p0 ORDER BY device_class, device_name;",
                               cancellationToken, snapshot).ConfigureAwait(false))
            {
                drivers.Add(new InstalledDriver(Text(row, 0), Text(row, 1), Text(row, 2), Text(row, 3), Text(row, 4), Text(row, 5), Text(row, 6), Text(row, 7)));
            }
        }

        var (displayTs, display) = await LatestJsonAsync(connection, "DisplaySnapshots", cancellationToken).ConfigureAwait(false);
        var (hardwareTs, hardware) = await LatestJsonAsync(connection, "HardwareSnapshots", cancellationToken).ConfigureAwait(false);

        var availability = new List<AvailabilityInfo>();
        await foreach (var row in RowsAsync(connection, "SELECT series, availability, reason, since_ts FROM SensorAvailability ORDER BY series;", cancellationToken).ConfigureAwait(false))
        {
            availability.Add(new AvailabilityInfo(row.GetString(0), ((Availability)row.GetInt32(1)).ToString(), Text(row, 2), row.GetInt64(3)));
        }

        var sensors = await SensorsAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var ioDevices = await IoDevicesAsync(connection, cancellationToken).ConfigureAwait(false);
        return new SystemOverview(information, devices, driversTs, drivers, displayTs, display, hardwareTs, hardware, availability, sensors, ioDevices);
    }

    /// <summary>Hardware sensors and their newest values; with a prefix, only those whose logical key starts with it (e.g. "lhm/lpc/" for the motherboard).</summary>
    public async Task<IReadOnlyList<SensorInfo>> SensorsAsync(string database, string? keyPrefix, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        return await SensorsAsync(connection, keyPrefix, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<SensorInfo>> SensorsAsync(SqliteConnection connection, string? keyPrefix, CancellationToken cancellationToken)
    {
        var sensors = new List<SensorInfo>();
        await foreach (var row in RowsAsync(connection,
                           """
                           SELECT c.provider, c.kind, c.name, c.unit, c.logical_key,
                                  (SELECT r.value FROM SensorReadings r WHERE r.sensor_id = c.sensor_id ORDER BY r.ts DESC LIMIT 1)
                           FROM SensorCatalog c WHERE $p1 IS NULL OR substr(c.logical_key, 1, length($p1)) = $p1 ORDER BY c.kind, c.name LIMIT $p0;
                           """,
                           cancellationToken, MaxSensors, keyPrefix).ConfigureAwait(false))
        {
            sensors.Add(new SensorInfo(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.IsDBNull(5) ? null : row.GetDouble(5)));
        }

        return sensors;
    }

    /// <summary>The newest device events (connections, drop-outs, failures), newest first.</summary>
    public async Task<IReadOnlyList<DeviceEventInfo>> DeviceEventsAsync(string database, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var events = new List<DeviceEventInfo>();
        await foreach (var row in RowsAsync(connection,
                           "SELECT ts, event_id, title, message, severity, related_device, data_json FROM WindowsEvents WHERE category = $p0 AND channel = $p1 ORDER BY ts DESC LIMIT $p2;",
                           cancellationToken, (int)Core.Events.EventCategory.Device, Core.Devices.DeviceEvents.Channel, Math.Clamp(limit, 1, 1000)).ConfigureAwait(false))
        {
            events.Add(new DeviceEventInfo(row.GetInt64(0), row.GetInt32(1), row.GetString(2), Text(row, 3), row.GetInt32(4), Text(row, 5), HistoryReader.ParseJson(Text(row, 6))));
        }

        return events;
    }

    /// <summary>
    /// The newest device register (every write lists all its devices, so older rows are devices it no longer lists);
    /// empty for a database from before the register existed.
    /// </summary>
    private static async Task<List<RegisteredDevice>> IoDevicesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new List<RegisteredDevice>();
        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'IoDevices';", cancellationToken).ConfigureAwait(false) is not > 0)
        {
            return result;
        }

        await foreach (var row in RowsAsync(connection, "SELECT details_json, first_seen, last_seen FROM IoDevices WHERE updated = (SELECT MAX(updated) FROM IoDevices) ORDER BY present DESC, technical, kind, name LIMIT $p0;", cancellationToken, Core.Devices.IoDevices.MaxDevices * 2)
                           .ConfigureAwait(false))
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<Core.Devices.IoDevice>(row.GetString(0), DeviceJson) is { } device)
                {
                    result.Add(new RegisteredDevice(device, row.GetInt64(1), row.GetInt64(2)));
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A row written by a newer version that this one cannot read.
            }
        }

        return result;
    }

    private static string? Text(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);

    private static async Task<(long? Ts, System.Text.Json.JsonElement? Json)> LatestJsonAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await foreach (var row in RowsAsync(connection, $"SELECT ts, json FROM {table} ORDER BY ts DESC LIMIT 1;", cancellationToken).ConfigureAwait(false))
        {
            return (row.GetInt64(0), HistoryReader.ParseJson(row.GetString(1)));
        }

        return (null, null);
    }

    private static async Task<long?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static async IAsyncEnumerable<SqliteDataReader> RowsAsync(
        SqliteConnection connection, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, params object?[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", parameters[i] ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return reader;
        }
    }
}
