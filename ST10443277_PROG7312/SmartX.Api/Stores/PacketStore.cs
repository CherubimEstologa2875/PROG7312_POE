using System.Globalization;
using System.Text.Json;
using SmartX.Api.Validation;
using SmartX.Contracts;

namespace SmartX.Api.Stores;

public sealed class PacketStore
{
    public const int CapacityPerType = 3000;

    private readonly object gate = new();
    private readonly string path;
    private PacketHistory history;

    public PacketStore(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        path = Path.GetFullPath(
            configuration["Packets:DataFile"] ??
            "App_Data/packets.json",
            environment.ContentRootPath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        history = File.Exists(path)
            ? JsonSerializer.Deserialize<PacketHistory>(
                File.ReadAllText(path))
                ?? throw new InvalidDataException(
                    "Invalid packet archive.")
            : new PacketHistory();

        // Reject structurally invalid persisted packet data rather than
        // silently replacing it with an empty history.
        if (history.Temperature == null ||
            history.Power == null ||
            history.Actuator == null)
        {
            throw new InvalidDataException(
                "Missing typed packet channel.");
        }

        CheckStored(
            history.Temperature,
            v =>
                float.IsFinite(v) &&
                v >= -273.15f &&
                v <= 10000);

        CheckStored(
            history.Power,
            v => v >= 0);

        CheckStored(
            history.Actuator,
            _ => true);
    }

    /* Applies the same structural validation algorithm to each typed packet
     channel. Generics allow one implementation to operate with different
     concrete payload types (Nakov et al., 2013).*/

    private static void CheckStored<T>(
        List<TelemetryPacket<T>> packets,
        Func<T, bool> valid)
        where T : struct
    {
        if (packets.Count > CapacityPerType ||
            packets.Any(p =>
                p == null ||
                string.IsNullOrWhiteSpace(p.Id) ||
                string.IsNullOrWhiteSpace(p.SensorId) ||
                string.IsNullOrWhiteSpace(p.Location) ||
                !p.Value.HasValue ||
                !valid(p.Value.Value) ||
                !TelemetryValidation.TryTimestamp(
                    p.Timestamp,
                    out _)))
        {
            throw new InvalidDataException(
                "Invalid stored typed packet.");
        }
    }

    public PacketHistory Read()
    {
        lock (gate)
            return history;
    }


    /* Adds a packet to its typed channel without converting the payload to
     object or double.
     The generic algorithm preserves the concrete telemetry value type while
     allowing callers to supply the channel-specific selection and
     replacement operations (Nakov et al., 2013).*/

    public TelemetryPacket<T> Add<T>(
        TelemetryPacket<T> packet,
        Func<PacketHistory, List<TelemetryPacket<T>>> select,
        Action<PacketHistory, List<TelemetryPacket<T>>> replace)
        where T : struct
    {
        lock (gate)
        {
            var channel =
                select(history);

            if (channel.Count == CapacityPerType &&
                string.CompareOrdinal(
                    packet.Timestamp,
                    channel[0].Timestamp) < 0)
            {
                throw new InvalidOperationException(
                    "The packet is older than the retained history.");
            }

            packet.Id =
                Guid.NewGuid().ToString("N");

            /* The retained sequence is staged through the shared jagged-array
             batching structure. Jagged arrays allow the final inner array
             to contain fewer elements than previous arrays
             (Nakov et al., 2013).*/
            var batches =
                TelemetryBatches<T>.Stage(
                    channel
                        .Append(packet)
                        .OrderBy(
                            p => p.Timestamp,
                            StringComparer.Ordinal)
                        .TakeLast(CapacityPerType));

            var next =
                new PacketHistory
                {
                    Temperature = history.Temperature,
                    Power = history.Power,
                    Actuator = history.Actuator
                };

            replace(
                next,
                TelemetryBatches<T>.ToHistory(batches));

            string temporary =
                path + ".tmp";

            try
            {
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(next));

                File.Move(
                    temporary,
                    path,
                    true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            history = next;

            return packet;
        }
    }


    /* Validates a typed packet and associates validation messages with their
     corresponding field names using a dictionary. Dictionaries represent
     key-value associations (Nakov et al., 2013).*/

    public static Dictionary<string, string[]> Validate<T>(
        TelemetryPacket<T> packet,
        SensorRegistrationStore sensors,
        string category,
        Func<T, bool> valid,
        string range)
        where T : struct
    {
        var identity =
            new RegisterSensorRequest
            {
                DeviceId = packet.SensorId,
                Location = packet.Location,
                Category = category
            };

        var errors =
            SensorRegistrationRules.Validate(identity);

        packet.SensorId =
            identity.DeviceId;

        packet.Location =
            identity.Location;

        var sensor =
            sensors.Read()
                .FirstOrDefault(
                    s => s.DeviceId.Equals(
                        packet.SensorId,
                        StringComparison.OrdinalIgnoreCase));

        if (sensor == null)
        {
            errors["SensorId"] =
                ["Register this sensor before submitting typed packets."];
        }
        else
        {
            packet.SensorId =
                sensor.DeviceId;

            if (sensor.Category != category ||
                sensor.Location != packet.Location)
            {
                errors["Profile"] =
                    ["Packet location and type must match the registered sensor profile."];
            }
        }

        if (!packet.Value.HasValue ||
            !valid(packet.Value.Value))
        {
            errors["Value"] = [range];
        }

        if (!TelemetryValidation.TryTimestamp(
                packet.Timestamp,
                out var time) ||
            time > DateTimeOffset.UtcNow)
        {
            errors["Timestamp"] =
                ["Use a non-future ISO 8601 timestamp with UTC offset."];
        }
        else
        {
            // Store validated timestamps in one canonical UTC form.
            packet.Timestamp =
                time
                    .ToUniversalTime()
                    .ToString(
                        "O",
                        CultureInfo.InvariantCulture);
        }

        return errors;
    }
}
// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
