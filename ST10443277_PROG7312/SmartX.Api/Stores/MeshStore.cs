using SmartX.Contracts;

namespace SmartX.Api.Stores;

public sealed class MeshStore
{
    private readonly TelemetryStore telemetry;
    private readonly SensorRegistrationStore registrations;
    public MeshStore(TelemetryStore telemetry, SensorRegistrationStore registrations) { this.telemetry = telemetry; this.registrations = registrations; }

    public MeshTopology Read(string? selected = null)
    {
        // Build the topology from the latest telemetry state and registered
        // devices so the dashboard reflects the current mesh membership.
        var states = telemetry.ReadDeviceStates();
        var registered = registrations.Read();
        var nodes = new List<MeshNode> { new MeshNode { Id = "gateway", Label = "Central Gateway", Location = "Control room", Gateway = true, Online = true, X = 50, Y = 50 } };
        var source = registered.Length > 0
            ? registered.Select(r => new { Id = r.DeviceId, Location = r.Location, Online = states.FirstOrDefault(s => s.DeviceId == r.DeviceId)?.Online ?? false }).ToList()
            : states.Select(s => new { Id = s.DeviceId, Location = s.Location, Online = s.Online }).ToList();
        int index = 0;
        foreach (var item in source.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase))
        { double angle = (Math.PI * 2 * index++) / Math.Max(1, source.Count); nodes.Add(new MeshNode { Id = item.Id, Label = item.Id, Location = item.Location, Online = item.Online, X = 50 + 38 * Math.Cos(angle), Y = 50 + 38 * Math.Sin(angle) }); }
        // Keep the physical graph intact for diagnosis, including offline
        // sensors. Availability is applied only to traversal and routing.
        var physicalGraph = MeshRouting.BuildCompleteGraph(nodes);
        var tree = MeshRouting.MinimumSpanningTree(nodes);

        // BFS reports the gateway-connected online subgraph. This allows the
        // UI to distinguish an offline node from an unreachable node.
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
            // Recalculate the MST using only gateway/online nodes. A failed
            // sensor can remain visible without becoming a routing hop.
            var activeNodes = nodes.Where(n => n.Gateway || n.Online).ToList();
            var activeTree = MeshRouting.MinimumSpanningTree(activeNodes);
            topology.Route = MeshRouting.ShortestRoute(activeNodes, activeTree, target, "gateway");
            topology.RouteStatus = topology.Route.Count == 0 ? "No active route to the gateway." : "Active route uses online nodes only.";
        }
        return topology;
    }
}
//REFERENCE LIST
//Choosing the right mesh networking technology for your application – Digi International Inc..2017. YouTube video, added by Digi International Inc.. [Online]. Available at: https://www.youtube.com/watch?v=cV5ktpaJv5Y&t=15s [Accessed 9 November 2026].
//How do you calculate a minimum spanning tree? – Spanning Tree.2020. YouTube video, added by Spanning Tree. [Online]. Available at: https://youtu.be/Yldkh0aOEcg?si=8CIHMlKIY4h5Ypba [Accessed 9 November 2026].
//Union find Krushal's algorithm – WilliamFiset.2017. YouTube video, added by WilliamFiset. [Online]. Available at: https://youtu.be/JZBQLXgSGfs?si=2u6KcV-Fa4a5_ToK [Accessed 9 November 2026].
