using SmartX.Contracts;

namespace SmartX.Api.Stores;

public sealed class PredictiveActionEngine
{
    private const int Capacity = 500;
    // Bounded queues retain recent search and command behaviour without
    // allowing the predictive component to grow indefinitely. Transitions are
    // counted in a dictionary so repeated workflows can produce a confidence
    // scored suggestion (YouTube, [s.a.]-c; YouTube, [s.a.]-d).
    // Sources: https://youtu.be/wHKlP8cov9w; https://youtu.be/ySd_-h_Dapc
    private readonly object gate = new();
    private readonly Queue<SearchActivity> searchHistory = new();
    private readonly Queue<DeviceCommand> commandHistory = new();
    private readonly Dictionary<string, int> commandFrequency = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> transitions = new(StringComparer.OrdinalIgnoreCase);

    public void RecordSearch(SearchActivity activity)
    {
        if (string.IsNullOrWhiteSpace(activity.Query) && string.IsNullOrWhiteSpace(activity.Category)) return;
        lock (gate)
        {
            var current = Normalise(activity);
            if (searchHistory.Count > 0)
            {
                var previous = Normalise(searchHistory.Last());
                var key = previous + " -> " + current;
                transitions[key] = transitions.TryGetValue(key, out var count) ? count + 1 : 1;
            }
            searchHistory.Enqueue(activity);
            while (searchHistory.Count > Capacity) searchHistory.Dequeue();
        }
    }

    public InsightResponse Analyse(IEnumerable<DeviceState> states)
    {
        lock (gate)
        {
            var suggestions = new List<SuggestedAction>();
            foreach (var state in states)
            {
                if (state.ActiveAlert)
                {
                    var action = state.Category == "Power" ? "TurnOff" : state.Category == "Humidity" && state.Value < 20 ? "TurnOn" : "";
                    var parameters = state.Category == "Humidity" ? "reason=low-moisture" : "reason=threshold-alert";
                    suggestions.Add(new SuggestedAction
                    {
                        DeviceId = state.DeviceId,
                        Title = action.Length == 0 ? "Investigate " + state.DeviceId : "Suggested " + action + " for " + state.DeviceId,
                        Explanation = DescribeAlert(state), SuggestedCommand = action, Parameters = parameters,
                        Severity = state.Category == "Power" ? "Critical" : "Warning", Confidence = action.Length == 0 ? 75 : 90
                    });
                }
            }

            var pumpPattern = transitions.Where(pair => pair.Key.IndexOf("moisture", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.IndexOf("humidity", StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(pair => pair.Key.IndexOf("pump", StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(pair => pair.Value).FirstOrDefault();
            if (!string.IsNullOrEmpty(pumpPattern.Key) && pumpPattern.Value >= 2)
            {
                suggestions.Insert(0, new SuggestedAction
                {
                    DeviceId = "",
                    Title = "Workflow pattern detected: inspect a pump",
                    Explanation = "Your recent searches often move from a low-moisture/humidity sensor to a pump. Check pump and relay status proactively.",
                    SuggestedCommand = "", Severity = "Insight", Confidence = Math.Min(99, 60 + pumpPattern.Value * 10)
                });
            }
            return new InsightResponse
            {
                Suggestions = suggestions.OrderByDescending(s => s.Severity == "Critical").ThenByDescending(s => s.Confidence).Take(12).ToList(),
                SearchEvents = searchHistory.Count,
                PatternSummary = transitions.Count == 0 && commandHistory.Count == 0 ? "Building a pattern from your searches and commands." : transitions.Count + " search transitions • " + commandHistory.Count + " manual commands analysed."
            };
        }
    }

    public void RecordCommand(DeviceCommand command)
    {
        lock (gate)
        {
            commandHistory.Enqueue(command);
            var key = command.DeviceId + ":" + command.Command;
            commandFrequency[key] = commandFrequency.TryGetValue(key, out var count) ? count + 1 : 1;
            while (commandHistory.Count > Capacity) commandHistory.Dequeue();
        }
    }

    private static string Normalise(SearchActivity activity) => ((activity.Query ?? "") + " " + (activity.Category ?? "")).Trim().ToLowerInvariant();
    private static string DescribeAlert(DeviceState state) => state.Category == "Power" ? state.DeviceId + " is reporting " + state.Value.ToString("0.##") + " W, above the safe threshold." : state.Category == "Humidity" && state.Value < 20 ? state.DeviceId + " is below 20% moisture; a pump or irrigation relay may be required." : state.DeviceId + " is outside its configured operating threshold.";
}
