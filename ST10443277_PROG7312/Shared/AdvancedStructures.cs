using System;
using System.Collections.Generic;

#pragma warning disable 8600, 8618, 8625

namespace SmartX.Contracts
{
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
        public void Add(DateTimeOffset key, TelemetryReading value) { bool added = false; root = Insert(root, key, value, ref added); if (added) Count++; }
        public List<TelemetryReading> Range(DateTimeOffset from, DateTimeOffset to) { var result = new List<TelemetryReading>(); VisitRange(root, from, to, result); return result; }
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
        private static int Height(Node node) { return node == null ? 0 : node.Height; }
        private static void SetHeight(Node node) { node.Height = Math.Max(Height(node.Left), Height(node.Right)) + 1; }
        private static int BalanceFactor(Node node) { return node == null ? 0 : Height(node.Left) - Height(node.Right); }
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
            var known = new HashSet<string>(); foreach (var node in nodes) known.Add(node.Id);
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
    }
}
