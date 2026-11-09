using SmartX.Contracts;

namespace SmartX.Api.Stores;

public sealed class MeshStore
{
    private readonly TelemetryStore telemetry;
    private readonly SensorRegistrationStore registrations;
    public MeshStore(TelemetryStore telemetry, SensorRegistrationStore registrations) { this.telemetry = telemetry; this.registrations = registrations; }

    public MeshTopology Read(string? selected = null)
    {
        var states = telemetry.ReadDeviceStates();
        var registered = registrations.Read();
        var nodes = new List<MeshNode> { new MeshNode { Id = "gateway", Label = "Central Gateway", Location = "Control room", Gateway = true, Online = true, X = 50, Y = 50 } };
        var source = registered.Length > 0
            ? registered.Select(r => new { Id = r.DeviceId, Location = r.Location, Online = states.FirstOrDefault(s => s.DeviceId == r.DeviceId)?.Online ?? false }).ToList()
            : states.Select(s => new { Id = s.DeviceId, Location = s.Location, Online = s.Online }).ToList();
        int index = 0;
        foreach (var item in source.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase))
        { double angle = (Math.PI * 2 * index++) / Math.Max(1, source.Count); nodes.Add(new MeshNode { Id = item.Id, Label = item.Id, Location = item.Location, Online = item.Online, X = 50 + 38 * Math.Cos(angle), Y = 50 + 38 * Math.Sin(angle) }); }
        var physicalGraph = MeshRouting.BuildCompleteGraph(nodes);
        var tree = MeshRouting.MinimumSpanningTree(nodes);
        var reachable = MeshGraphTraversal.BreadthFirst(nodes, physicalGraph, "gateway", node => node.Gateway || node.Online);
        var recoveryHops = MeshGraphTraversal.FindRecoveryHops(nodes, physicalGraph);
        var topology = new MeshTopology
        {
            Nodes = nodes,
            Links = physicalGraph,
            SpanningTreeLinks = tree,
            ReachableFromGateway = reachable,
            DownNodes = nodes.Where(n => !n.Gateway && !n.Online).Select(n => n.Id).ToList(),
            RecoveryHops = recoveryHops,
            SpanningTreeCost = tree.Sum(l => l.Weight)
        };
        string? target = string.IsNullOrWhiteSpace(selected) ? null : nodes.FirstOrDefault(n => n.Id.Equals(selected, StringComparison.OrdinalIgnoreCase))?.Id;
        if (target == null) topology.RouteStatus = "No sensor selected.";
        else if (!nodes.First(n => n.Id == target).Online) topology.RouteStatus = "Sensor is down. Recovery hops: " + string.Join(", ", recoveryHops.ContainsKey(target) ? recoveryHops[target] : new List<string> { "none available" });
        else
        {
            var activeNodes = nodes.Where(n => n.Gateway || n.Online).ToList();
            var activeTree = MeshRouting.MinimumSpanningTree(activeNodes);
            topology.Route = MeshRouting.ShortestRoute(activeNodes, activeTree, target, "gateway");
            topology.RouteStatus = topology.Route.Count == 0 ? "No active route to the gateway." : "Active route uses online nodes only.";
        }
        return topology;
    }
}
