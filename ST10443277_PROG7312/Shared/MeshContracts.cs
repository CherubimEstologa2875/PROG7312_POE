using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartX.Contracts
{
    public class MeshNode
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Location { get; set; } = "";
        public bool Gateway { get; set; }
        public bool Online { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class MeshLink
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public double Weight { get; set; }
        public bool InSpanningTree { get; set; }
    }

    public class MeshRouteHop
    {
        public string NodeId { get; set; } = "";
        public string Label { get; set; } = "";
        public double CumulativeCost { get; set; }
    }

    public class MeshTopology
    {
        public string GatewayId { get; set; } = "gateway";
        public List<MeshNode> Nodes { get; set; } = new List<MeshNode>();
        public List<MeshLink> Links { get; set; } = new List<MeshLink>();
        public List<MeshLink> SpanningTreeLinks { get; set; } = new List<MeshLink>();
        public List<string> ReachableFromGateway { get; set; } = new List<string>();
        public List<string> DownNodes { get; set; } = new List<string>();
        // Down sensors remain visible in the physical graph, but routing uses
        // the online subgraph. These are the next-hop recovery candidates.
        public Dictionary<string, List<string>> RecoveryHops { get; set; } = new Dictionary<string, List<string>>();
        public List<MeshRouteHop> Route { get; set; } = new List<MeshRouteHop>();
        public string RouteStatus { get; set; } = "No sensor selected.";
        public double SpanningTreeCost { get; set; }
    }

    // A small binary min-heap. It is used by Dijkstra's shortest-path search.
    public sealed class MinHeap<T>
    {
        private readonly List<Tuple<double, T>> items = new List<Tuple<double, T>>();
        public int Count { get { return items.Count; } }
        public void Enqueue(T value, double priority)
        {
            items.Add(Tuple.Create(priority, value));
            int i = items.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (items[p].Item1 <= items[i].Item1) break;
                var t = items[p]; items[p] = items[i]; items[i] = t; i = p;
            }
        }
        public T Dequeue()
        {
            if (items.Count == 0) throw new InvalidOperationException("The heap is empty.");
            T result = items[0].Item2;
            var last = items[items.Count - 1]; items.RemoveAt(items.Count - 1);
            if (items.Count > 0)
            {
                items[0] = last; int i = 0;
                while (true)
                {
                    int left = i * 2 + 1, right = left + 1, smallest = i;
                    if (left < items.Count && items[left].Item1 < items[smallest].Item1) smallest = left;
                    if (right < items.Count && items[right].Item1 < items[smallest].Item1) smallest = right;
                    if (smallest == i) break;
                    var t = items[i]; items[i] = items[smallest]; items[smallest] = t; i = smallest;
                }
            }
            return result;
        }
    }

    public static class MeshRouting
    {
        public static List<MeshLink> BuildCompleteGraph(IEnumerable<MeshNode> nodes)
        {
            // The simulated wireless graph is intentionally sparse: a mesh
            // ring plus short-range skip links avoids O(n^2) edge generation
            // when the gateway manages thousands of sensors.
            var list = nodes.ToList(); var links = new List<MeshLink>();
            for (int i = 0; i < list.Count; i++)
            {
                AddLink(links, list[i], list[(i + 1) % list.Count]);
                if (list.Count > 3) AddLink(links, list[i], list[(i + 2) % list.Count]);
            }
            return links;
        }

        private static void AddLink(List<MeshLink> links, MeshNode a, MeshNode b)
        {
            if (a.Id == b.Id || links.Any(link => (link.From == a.Id && link.To == b.Id) || (link.From == b.Id && link.To == a.Id))) return;
            double dx = a.X - b.X, dy = a.Y - b.Y;
            links.Add(new MeshLink { From = a.Id, To = b.Id, Weight = Math.Round(Math.Sqrt(dx * dx + dy * dy), 2) });
        }

        public static List<MeshLink> MinimumSpanningTree(IEnumerable<MeshNode> nodes)
        {
            var list = nodes.ToList(); var links = BuildCompleteGraph(list);
            links = links.OrderBy(l => l.Weight).ToList();
            var parent = list.ToDictionary(n => n.Id, n => n.Id);
            foreach (var link in links)
            {
                string a = Root(parent, link.From), b = Root(parent, link.To);
                if (a == b) continue;
                parent[a] = b; link.InSpanningTree = true;
            }
            return links.Where(l => l.InSpanningTree).ToList();
        }

        public static List<MeshRouteHop> ShortestRoute(IEnumerable<MeshNode> nodes, IEnumerable<MeshLink> tree, string start, string destination)
        {
            var nodeMap = nodes.ToDictionary(n => n.Id);
            var edges = tree.ToList();
            var distance = nodeMap.Keys.ToDictionary(id => id, id => double.PositiveInfinity);
            var previous = new Dictionary<string, string>();
            var heap = new MinHeap<string>(); distance[start] = 0; heap.Enqueue(start, 0);
            while (heap.Count > 0)
            {
                string current = heap.Dequeue();
                foreach (var edge in edges.Where(e => e.From == current || e.To == current))
                {
                    string next = edge.From == current ? edge.To : edge.From;
                    double candidate = distance[current] + edge.Weight;
                    if (candidate < distance[next]) { distance[next] = candidate; previous[next] = current; heap.Enqueue(next, candidate); }
                }
            }
            var route = new List<string>();
            if (!distance.ContainsKey(destination) || double.IsPositiveInfinity(distance[destination])) return new List<MeshRouteHop>();
            for (string at = destination; ; at = previous[at]) { route.Add(at); if (at == start) break; }
            route.Reverse();
            return route.Select(id => new MeshRouteHop { NodeId = id, Label = nodeMap[id].Label, CumulativeCost = distance[id] }).ToList();
        }

        private static string Root(Dictionary<string, string> parent, string value)
        { while (parent[value] != value) { parent[value] = parent[parent[value]]; value = parent[value]; } return value; }
    }
}
