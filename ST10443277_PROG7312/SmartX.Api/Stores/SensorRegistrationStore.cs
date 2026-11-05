using System.Globalization;
using System.Text.Json;
using SmartX.Contracts;

namespace SmartX.Api.Stores;

// Stores sensor registrations independently from telemetry history so a
// registered sensor remains known even when older telemetry is discarded.
public sealed class SensorRegistrationStore
{
    private readonly object gate = new();
    private readonly string path;
    private List<SensorRegistration> sensors;

    public SensorRegistrationStore(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        path = Path.GetFullPath(
            configuration["Registration:DataFile"] ??
            "App_Data/sensors.json",
            environment.ContentRootPath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        sensors = File.Exists(path)
            ? JsonSerializer.Deserialize<List<SensorRegistration>>(
                File.ReadAllText(path))
                ?? throw new InvalidDataException(
                    "The sensor registry is not a registration list.")
            : [];

        // Validate persisted registrations at startup, including the
        // uniqueness of both public device identifiers and internal IDs.
        if (sensors.Any(s =>
                s == null ||
                string.IsNullOrWhiteSpace(s.Id) ||
                !DateTimeOffset.TryParse(
                    s.RegisteredAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _) ||
                SensorRegistrationRules.Validate(s).Count != 0) ||
            sensors
                .Select(s => s.DeviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != sensors.Count ||
            sensors
                .Select(s => s.Id)
                .Distinct()
                .Count() != sensors.Count)
        {
            throw new InvalidDataException(
                "The sensor registry contains invalid or duplicate records.");
        }
    }

    public SensorRegistration[] Read()
    {
        lock (gate)
        {
            return sensors
                .OrderBy(
                    s => s.DeviceId,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public SensorRegistration Add(
        RegisterSensorRequest request)
    {
        lock (gate)
        {
            // Device identifiers are unique regardless of letter casing.
            if (sensors.Any(
                s => s.DeviceId.Equals(
                    request.DeviceId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "This device is already registered. Use a different device identifier.");
            }

            if (sensors.Count >= 10000)
            {
                throw new InvalidOperationException(
                    "The registry has reached its 10,000-sensor capacity.");
            }

            var sensor =
                new SensorRegistration
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DeviceId = request.DeviceId,
                    Location = request.Location,
                    Category = request.Category,
                    RegisteredAt =
                        DateTimeOffset.UtcNow.ToString(
                            "O",
                            CultureInfo.InvariantCulture)
                };

            var next =
                sensors
                    .Append(sensor)
                    .ToList();

            string temporary =
                path + ".tmp";

            try
            {
                // Write the complete next registry to a temporary file before
                // replacing the current persisted version.
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(next));

                File.Move(
                    temporary,
                    path,
                    overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            sensors = next;

            return sensor;
        }
    }
}
// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
