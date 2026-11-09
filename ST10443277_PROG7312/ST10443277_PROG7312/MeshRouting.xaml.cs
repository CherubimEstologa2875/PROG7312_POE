using SmartX.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ST10443277_PROG7312
{
    public partial class MeshRouting : UserControl
    {
        public event EventHandler BackRequested;
        private MeshTopology topology;
        private TelemetryApiClient api;
        private readonly List<TelemetryReading> history = new List<TelemetryReading>();
        public MeshRouting() { InitializeComponent(); Loaded += async (s, e) => await ReloadAsync(); }
        private async void Refresh_Click(object sender, RoutedEventArgs e) { await ReloadAsync(); }
        private async void Sensor_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await ReloadAsync(); }
        private async Task ReloadAsync()
        {
            try
            {
                if (api == null) api = new TelemetryApiClient();
                string selected = SensorSelector.SelectedItem as string;
                topology = await api.GetMeshAsync(selected);
                history.Clear();
                if (!string.IsNullOrWhiteSpace(selected))
                    history.AddRange(await api.GetTelemetryRangeAsync(selected, DateTimeOffset.UtcNow.AddHours(-24), DateTimeOffset.UtcNow));
                SensorSelector.ItemsSource = topology.Nodes.Where(n => !n.Gateway).Select(n => n.Id).ToList(); SensorSelector.SelectedItem = selected;
                SummaryText.Text = topology.Nodes.Count + " nodes • graph " + topology.Links.Count + " links • MST " + topology.SpanningTreeLinks.Count + " links • BFS reached " + topology.ReachableFromGateway.Count + " • down " + topology.DownNodes.Count;
                HistoryList.ItemsSource = history; HistoryStatus.Text = history.Count + " readings returned by the AVL tree range query (last 24 hours) for " + (selected ?? "the selected sensor") + ".";
                RouteList.ItemsSource = topology.Route.Select(r => r.Label + "  (cost " + r.CumulativeCost.ToString("0.0") + ")"); RouteText.Text = topology.Route.Count == 0 ? "Select a sensor to trace its route." : "Shortest path to Central Gateway"; DrawNetwork();
            }
            catch (Exception error) { SummaryText.Text = "API unavailable: " + error.Message; }
        }
        private void DrawNetwork()
        {
            if (topology == null) return; NetworkCanvas.Children.Clear(); double w = Math.Max(300, NetworkCanvas.ActualWidth), h = Math.Max(200, NetworkCanvas.ActualHeight); var routeIds = new HashSet<string>(topology.Route.Select(r => r.NodeId));
            foreach (var link in topology.Links) { var a = topology.Nodes.First(n => n.Id == link.From); var b = topology.Nodes.First(n => n.Id == link.To); bool inTree = topology.SpanningTreeLinks.Any(treeLink => (treeLink.From == link.From && treeLink.To == link.To) || (treeLink.From == link.To && treeLink.To == link.From)); var line = new Line { X1 = a.X / 100 * w, Y1 = a.Y / 100 * h, X2 = b.X / 100 * w, Y2 = b.Y / 100 * h, Stroke = routeIds.Contains(a.Id) && routeIds.Contains(b.Id) ? Brushes.Gold : inTree ? Brushes.MediumSpringGreen : new SolidColorBrush(Color.FromRgb(83, 119, 139)), StrokeThickness = routeIds.Contains(a.Id) && routeIds.Contains(b.Id) ? 4 : inTree ? 2.2 : 0.8 }; NetworkCanvas.Children.Add(line); }
            foreach (var node in topology.Nodes) { double x = node.X / 100 * w, y = node.Y / 100 * h; var dot = new Ellipse { Width = node.Gateway ? 26 : 18, Height = node.Gateway ? 26 : 18, Fill = node.Gateway ? Brushes.Gold : (node.Online ? Brushes.MediumSpringGreen : Brushes.IndianRed), Stroke = Brushes.White, StrokeThickness = 2, ToolTip = node.Label + "\n" + node.Location + "\n" + (node.Online ? "Online" : "Offline") }; Canvas.SetLeft(dot, x - dot.Width / 2); Canvas.SetTop(dot, y - dot.Height / 2); NetworkCanvas.Children.Add(dot); var label = new TextBlock { Text = node.Label, Foreground = Brushes.White, FontSize = 11, FontWeight = routeIds.Contains(node.Id) ? FontWeights.Bold : FontWeights.Normal }; Canvas.SetLeft(label, x + 10); Canvas.SetTop(label, y - 8); NetworkCanvas.Children.Add(label); }
        }
        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) { DrawNetwork(); }
        private void Back_Click(object sender, RoutedEventArgs e) { BackRequested?.Invoke(this, EventArgs.Empty); }
    }
}
