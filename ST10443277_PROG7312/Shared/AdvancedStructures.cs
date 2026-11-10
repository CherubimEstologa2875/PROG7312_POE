using System;
using System.Collections.Generic;
using System.Linq;

#pragma warning disable 8600, 8618, 8625

namespace SmartX.Contracts
{
    // An AVL tree is a binary search tree whose height is kept logarithmic by
    // rotations after insertion. Each sensor owns one tree keyed by the
    // timestamp, so a range query visits only relevant branches plus matches.
    // This gives O(log n + k) querying for k returned readings instead of
    // scanning the complete retained telemetry list.
    public sealed class TelemetryAvlTree
    {
        private sealed class Node
        {
            public DateTimeOffset Key;
            public List<TelemetryReading> Values = new List<TelemetryReading>();
            public Node Left = (Node)(object)null;
            public Node Right = (Node)(object)null;
            public int Height = 1;
            public Node(DateTimeOffset key, TelemetryReading value) { Key = key; Values.Add(value); }
        }
        private Node root = (Node)(object)null;
        public int Count { get; private set; }
        public int Height { get { return HeightOf(root); } }
        public void Add(DateTimeOffset key, TelemetryReading value) { bool added = false; root = Insert(root, key, value, ref added); if (added) Count++; }
        public List<TelemetryReading> Range(DateTimeOffset from, DateTimeOffset to)
        {
            var result = new List<TelemetryReading>();
            if (from > to) return result;
            VisitRange(root, from, to, result);
            return result;
        }
        public bool IsBalanced() { return IsBalanced(root); }
        private static Node Insert(Node node, DateTimeOffset key, TelemetryReading value, ref bool added)
        {
            if (node == null) { added = true; return new Node(key, value); }
            int compare = key.CompareTo(node.Key);
            if (compare == 0) node.Values.Add(value); else if (compare < 0) node.Left = Insert(node.Left, key, value, ref added); else node.Right = Insert(node.Right, key, value, ref added);
            return Balance(node);
        }
        private static void VisitRange(Node node, DateTimeOffset from, DateTimeOffset to, List<TelemetryReading> result)
        {
            if (node == null) return;
            if (node.Key > from) VisitRange(node.Left, from, to, result);
            if (node.Key >= from && node.Key <= to) result.AddRange(node.Values);
            if (node.Key < to) VisitRange(node.Right, from, to, result);
        }
        private static int HeightOf(Node node) { return node == null ? 0 : node.Height; }
        private static void SetHeight(Node node) { node.Height = Math.Max(HeightOf(node.Left), HeightOf(node.Right)) + 1; }
        private static int BalanceFactor(Node node) { return node == null ? 0 : HeightOf(node.Left) - HeightOf(node.Right); }
        private static bool IsBalanced(Node node)
        {
            if (node == null) return true;
            return Math.Abs(HeightOf(node.Left) - HeightOf(node.Right)) <= 1 && IsBalanced(node.Left) && IsBalanced(node.Right);
        }
        private static Node Balance(Node node)
        {
            SetHeight(node); int factor = BalanceFactor(node);
            if (factor > 1) { if (BalanceFactor(node.Left) < 0) node.Left = RotateLeft(node.Left); return RotateRight(node); }
            if (factor < -1) { if (BalanceFactor(node.Right) > 0) node.Right = RotateRight(node.Right); return RotateLeft(node); }
            return node;
        }
        private static Node RotateRight(Node node) { Node next = node.Left; node.Left = next.Right; next.Right = node; SetHeight(node); SetHeight(next); return next; }
        private static Node RotateLeft(Node node) { Node next = node.Right; node.Right = next.Left; next.Left = node; SetHeight(node); SetHeight(next); return next; }
    }

    public static class MeshGraphTraversal
    {
        public static List<string> BreadthFirst(IEnumerable<MeshNode> nodes, IEnumerable<MeshLink> links, string start)
        {
            return BreadthFirst(nodes, links, start, node => true);
        }

        // BFS over the active subgraph. Down sensors remain visible in the
        // physical graph, but are not treated as connected hops.
        public static List<string> BreadthFirst(IEnumerable<MeshNode> nodes, IEnumerable<MeshLink> links, string start, Func<MeshNode, bool> canTraverse)
        {
            var known = new HashSet<string>(nodes.Where(canTraverse).Select(node => node.Id), StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var queue = new Queue<string>(); var result = new List<string>();
            if (!known.Contains(start)) return result;
            queue.Enqueue(start); visited.Add(start);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue(); result.Add(current);
                foreach (var link in links)
                {
                    string next = link.From.Equals(current, StringComparison.OrdinalIgnoreCase) ? link.To : link.To.Equals(current, StringComparison.OrdinalIgnoreCase) ? link.From : "";
                    if (next.Length > 0 && visited.Add(next)) queue.Enqueue(next);
                }
            }
            return result;
        }

        public static Dictionary<string, List<string>> FindRecoveryHops(IEnumerable<MeshNode> nodes, IEnumerable<MeshLink> links)
        {
            var nodeMap = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var down in nodeMap.Values.Where(node => !node.Gateway && !node.Online))
            {
                result[down.Id] = links
                    .Where(link => link.From.Equals(down.Id, StringComparison.OrdinalIgnoreCase) || link.To.Equals(down.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(link => link.From.Equals(down.Id, StringComparison.OrdinalIgnoreCase) ? link.To : link.From)
                    .Where(nodeMap.ContainsKey)
                    .Where(id => nodeMap[id].Online || nodeMap[id].Gateway)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            return result;
        }
    }
}
