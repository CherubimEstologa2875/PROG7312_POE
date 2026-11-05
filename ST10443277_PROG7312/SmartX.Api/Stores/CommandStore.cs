using System.Text.Json;
using SmartX.Contracts;

namespace SmartX.Api.Stores;

public sealed class CommandStore
{
    public const int Capacity = 1000;
    private readonly object gate = new();
    private readonly string path;
    // Queue<T> preserves first-in, first-out command processing, while the
    // separate Stack<T> gives Undo its last-in, first-out behaviour. Keeping
    // these structures distinct makes each collection's purpose explicit
    // (YouTube, [s.a.]-b; YouTube, [s.a.]-c).
    // Sources: https://youtu.be/R94JHIXdTk0; https://youtu.be/wHKlP8cov9w
    private readonly Queue<DeviceCommand> queue = new();
    // LIFO history makes the most recent manual override available for Undo.
    private readonly Stack<DeviceCommand> manualOverrideHistory = new();
    private List<DeviceCommand> history;

    public CommandStore(IConfiguration configuration, IHostEnvironment environment)
    {
        path = Path.GetFullPath(configuration["Commands:DataFile"] ?? "App_Data/commands.json", environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        history = File.Exists(path)
            ? JsonSerializer.Deserialize<List<DeviceCommand>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid command archive.")
            : new List<DeviceCommand>();
        if (history.Count > Capacity || history.Any(c => c == null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.DeviceId)))
            throw new InvalidDataException("Invalid stored command.");
        foreach (var command in history.Where(c => c.Status == "Queued")) queue.Enqueue(command);
        foreach (var command in history.Where(c => c.Source != "Undo").OrderBy(c => c.CreatedAt)) manualOverrideHistory.Push(command);
    }

    public CommandHistory Read()
    {
        lock (gate) return new CommandHistory { Items = history.OrderByDescending(c => c.CreatedAt).ToList(), Pending = queue.Count };
    }

    public DeviceCommand Add(SubmitCommandRequest request)
    {
        lock (gate)
        {
            // The same command is retained in history and enqueued for work;
            // acknowledgement later changes its status without deleting the
            // audit record (YouTube, [s.a.]-a).
            // Source: https://youtu.be/A3ZUpyrnCbM
            var command = new DeviceCommand { Id = Guid.NewGuid().ToString("N"), DeviceId = request.DeviceId.Trim(), Command = request.Command.Trim(), Parameters = (request.Parameters ?? "").Trim(), Status = "Queued", Source = "Manual", CreatedAt = DateTimeOffset.UtcNow.ToString("O") };
            history.Add(command);
            queue.Enqueue(command);
            manualOverrideHistory.Push(command);
            if (history.Count > Capacity) history = history.TakeLast(Capacity).ToList();
            Persist();
            return command;
        }
    }

    public DeviceCommand? UndoLast()
    {
        lock (gate)
        {
            while (manualOverrideHistory.Count > 0)
            {
                var original = manualOverrideHistory.Pop();
                var inverse = InverseOf(original.Command);
                if (inverse == null) continue;
                var undo = new DeviceCommand
                {
                    Id = Guid.NewGuid().ToString("N"), DeviceId = original.DeviceId,
                    Command = inverse, Parameters = original.Parameters, Status = "Queued",
                    Source = "Undo", RevertedCommandId = original.Id,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O")
                };
                history.Add(undo); queue.Enqueue(undo); Persist(); return undo;
            }
            return null;
        }
    }

    private static string? InverseOf(string command) => command switch
    {
        "TurnOn" => "TurnOff",
        "TurnOff" => "TurnOn",
        _ => null
    };

    public DeviceCommand? Acknowledge(string id)
    {
        lock (gate)
        {
            var command = history.FirstOrDefault(c => c.Id == id);
            if (command == null) return null;
            command.Status = "Delivered";
            command.CompletedAt = DateTimeOffset.UtcNow.ToString("O");
            var remaining = queue.Where(c => c.Id != id).ToArray();
            queue.Clear(); foreach (var item in remaining) queue.Enqueue(item);
            Persist(); return command;
        }
    }

    private void Persist() => File.WriteAllText(path, JsonSerializer.Serialize(history));
}
