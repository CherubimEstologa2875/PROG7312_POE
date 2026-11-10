using System.Text.Json;
using SmartX.Api.Validation;
using SmartX.Contracts;

namespace SmartX.Api.Stores;

/* Stores validated telemetry readings.
 The store retains only the newest 10,000 readings so persisted storage
 and dashboard payloads remain bounded.*/
public sealed class TelemetryStore
{
    public const int Capacity = 10000;

    private readonly object gate = new();
    private readonly string path;
    private List<TelemetryReading> readings;
    // FIFO ingress buffer for ordinary telemetry packets.
    private readonly Queue<TelemetryReading> incomingQueue = new();
    // Lower priority values are processed first; critical packets bypass FIFO.
    private readonly PriorityQueue<TelemetryReading, int> criticalQueue = new();
    // O(1) latest-state lookup for registered/live devices.
    private readonly Dictionary<string, TelemetryReading> latestByDevice =
        new(StringComparer.OrdinalIgnoreCase);
    // Per-device AVL indexes support O(log n + k) historical range queries.
    private readonly Dictionary<string, TelemetryAvlTree> historyByDevice =
        new(StringComparer.OrdinalIgnoreCase);
    // Timestamp index for efficient chronological timeline rendering.
    private readonly SortedDictionary<DateTimeOffset, List<TelemetryReading>> logsByTimestamp = new();
    // De-duplicates repeated active alert/disconnect states.
    private readonly HashSet<string> activeErrorStates = new(StringComparer.OrdinalIgnoreCase);

    // These collections model different access patterns: FIFO ingestion,
    // priority handling for critical readings, constant-time latest-state
    // lookup, chronological indexing, and unique alert/disconnect membership.
    // HashSet is not used to represent categories or dates. The
    // collection choice is therefore part of the behaviour, not only an
    // implementation detail (YouTube, [s.a.]-b; YouTube, [s.a.]-d).
    // Sources: https://youtu.be/R94JHIXdTk0; https://youtu.be/ySd_-h_Dapc

    public TelemetryStore(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        path = Path.GetFullPath(
            configuration["Telemetry:DataFile"] ??
            "App_Data/telemetry.json",
            environment.ContentRootPath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        readings = File.Exists(path)
            ? JsonSerializer.Deserialize<List<TelemetryReading>>(
                File.ReadAllText(path))
                ?? throw new InvalidDataException(
                    "The telemetry data file is not a reading list.")
            : [];

        // Corrupt persisted data is reported explicitly instead of silently
        // replacing the telemetry history with an empty collection.
        if (readings.Any(r =>
            r == null ||
            string.IsNullOrWhiteSpace(r.Id) ||
            TelemetryValidation.Validate(r).Count != 0))
        {
            throw new InvalidDataException(
                "The telemetry data file contains invalid readings.");
        }

        // Enforce the retention policy when existing data is loaded.
        readings =
            readings
                .OrderBy(
                    r => r.Timestamp,
                    StringComparer.Ordinal)
                .TakeLast(Capacity)
                .ToList();
        RebuildDeviceIndex();
    }

    public TelemetryReading[] Read()
    {
        lock (gate)
            return readings.ToArray();
    }

    public DeviceState[] ReadDeviceStates()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            activeErrorStates.Clear();
            var states = latestByDevice.Values.Select(reading => new DeviceState
            {
                DeviceId = reading.SensorId,
                Location = reading.Location,
                Category = reading.Category,
                Value = reading.Value ?? 0,
                LastSeen = reading.Timestamp,
                Online = DateTimeOffset.TryParse(reading.Timestamp, out var seen) && now - seen <= TimeSpan.FromMinutes(5),
                ActiveAlert = IsAlert(reading)
            }).OrderBy(state => state.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var state in states)
            {
                if (state.ActiveAlert) activeErrorStates.Add("alert:" + state.DeviceId);
                if (!state.Online) activeErrorStates.Add("disconnected:" + state.DeviceId);
            }
            return states;
        }
    }

    // Public key-value view used by the dashboard's direct device lookup.
    public Dictionary<string, DeviceState> ReadDeviceDictionary()
    {
        return ReadDeviceStates().ToDictionary(state => state.DeviceId, StringComparer.OrdinalIgnoreCase);
    }

    public TimelineSummary ReadTimelineSummary()
    {
        lock (gate)
        {
            var ordered = logsByTimestamp.SelectMany(pair => pair.Value).ToArray();
            return new TimelineSummary
            {
                ReadingCount = ordered.Length,
                FirstTimestamp = ordered.Length == 0 ? "" : ordered[0].Timestamp,
                LastTimestamp = ordered.Length == 0 ? "" : ordered[ordered.Length - 1].Timestamp
            };
        }
    }

    public TelemetryReading[] ReadTimeline()
    {
        lock (gate) return logsByTimestamp.SelectMany(pair => pair.Value).ToArray();
    }

    public TelemetryReading[] ReadRange(string sensorId, DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate)
        {
            // The mesh history screen reaches this method through the range
            // endpoint. Querying the sensor's AVL tree avoids scanning every
            // retained reading in the system.
            if (!historyByDevice.TryGetValue(sensorId, out var tree)) return Array.Empty<TelemetryReading>();
            return tree.Range(from, to).OrderBy(reading => reading.Timestamp, StringComparer.Ordinal).ToArray();
        }
    }

    public TelemetryReading Add(
        SubmitTelemetryRequest request)
    {
        lock (gate)
        {
            // Once a sensor has supplied telemetry, its location and category
            // must remain consistent with its existing history.
            var existing =
                readings.FirstOrDefault(
                    r => r.SensorId == request.SensorId);

            if (existing != null &&
                (existing.Location != request.Location ||
                 existing.Category != request.Category))
            {
                throw new InvalidOperationException(
                    "This sensor already uses a different location or category. " +
                    "Use its existing details or a new sensor ID.");
            }

            // When storage is full, readings older than the retained window
            // are rejected because they would immediately be evicted.
            if (readings.Count == Capacity &&
                string.CompareOrdinal(
                    request.Timestamp,
                    readings[0].Timestamp) < 0)
            {
                throw new InvalidOperationException(
                    "This reading is older than the retained history.");
            }

            var reading =
                new TelemetryReading
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SensorId = request.SensorId,
                    Location = request.Location,
                    Category = request.Category,
                    Value = request.Value,
                    Timestamp = request.Timestamp
                };

            var next =
                readings
                    .Append(reading)
                    .OrderBy(
                        r => r.Timestamp,
                        StringComparer.Ordinal)
                    .TakeLast(Capacity)
                    .ToList();

            var temporary =
                path + ".tmp";

            try
            {
                // Persist the complete next state before replacing the
                // currently stored telemetry file.
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

            readings = next;
            RebuildHistoryIndex();
            EnqueueForProcessing(reading);
            ProcessIncomingQueues();
            TrimTimelineToRetention();

            return reading;
        }
    }

    private void RebuildDeviceIndex()
    {
        RebuildHistoryIndex();
        foreach (var reading in readings)
        {
            latestByDevice[reading.SensorId] = reading;
            if (DateTimeOffset.TryParse(reading.Timestamp, out var timestamp))
            {
                if (!logsByTimestamp.TryGetValue(timestamp, out var bucket)) logsByTimestamp[timestamp] = bucket = new List<TelemetryReading>();
                bucket.Add(reading);
            }
        }
    }

    private void RebuildHistoryIndex()
    {
        historyByDevice.Clear();
        foreach (var reading in readings)
        {
            if (DateTimeOffset.TryParse(reading.Timestamp, out var timestamp))
            {
                if (!historyByDevice.TryGetValue(reading.SensorId, out var tree)) historyByDevice[reading.SensorId] = tree = new TelemetryAvlTree();
                tree.Add(timestamp, reading);
            }
        }
    }

    private void EnqueueForProcessing(TelemetryReading reading)
    {
        if (IsAlert(reading)) criticalQueue.Enqueue(reading, CriticalPriority(reading));
        else incomingQueue.Enqueue(reading);
    }

    private void ProcessIncomingQueues()
    {
        // Critical data always drains before normal FIFO data.
        while (criticalQueue.TryDequeue(out var critical, out _)) ProcessState(critical);
        while (incomingQueue.TryDequeue(out var ordinary)) ProcessState(ordinary);
    }

    private void ProcessState(TelemetryReading reading)
    {
        latestByDevice[reading.SensorId] = reading;
        if (DateTimeOffset.TryParse(reading.Timestamp, out var timestamp))
        {
            if (!logsByTimestamp.TryGetValue(timestamp, out var bucket)) logsByTimestamp[timestamp] = bucket = new List<TelemetryReading>();
            bucket.Add(reading);
        }
        if (IsAlert(reading)) activeErrorStates.Add("alert:" + reading.SensorId);
    }

    private void TrimTimelineToRetention()
    {
        var retainedIds = new HashSet<string>(readings.Select(reading => reading.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var timestamp in logsByTimestamp.Keys.ToArray())
        {
            logsByTimestamp[timestamp].RemoveAll(reading => !retainedIds.Contains(reading.Id));
            if (logsByTimestamp[timestamp].Count == 0) logsByTimestamp.Remove(timestamp);
        }
    }

    private static int CriticalPriority(TelemetryReading reading) => reading.Category == "Power" ? 0 : 1;

    private static bool IsAlert(TelemetryReading reading)
    {
        if (!reading.Value.HasValue || reading.Category == "Actuator") return false;
        return reading.Category == "Power" ? reading.Value.Value > 2000 : reading.Value.Value > 75;
    }
}
// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
