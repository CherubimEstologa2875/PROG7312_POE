using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Input;
using SmartX.Contracts;

namespace ST10443277_PROG7312
{
    public partial class TelemetryDashboard : UserControl
    {
        // Only successful API responses replace the displayed snapshot.
        private DateTime snapshot = DateTime.Now;
        private readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private TelemetryApiClient api;
        private bool fetching;
        private bool refreshPending;
        private DateTime? lastSynced;
        private readonly List<SensorSnapshot> sensors = new List<SensorSnapshot>();
        private bool ready;
        private SensorSnapshot chartSensor;
        private List<Reading> visibleReadings = new List<Reading>();
        private DateTime rangeStart;

        public event EventHandler BackRequested;

        public TelemetryDashboard()
        {
            InitializeComponent();
            SensorFilter.ItemsSource = new[] { "All sensors" };
            LocationFilter.ItemsSource = new[] { "All locations" };
            CategoryFilter.ItemsSource = new[] { "All categories", "Temperature", "Humidity", "Power", "Actuator" };
            TimeFilter.ItemsSource = new[] { "Last hour", "Last 30 minutes", "Last 5 minutes" };
            SensorFilter.SelectedIndex = LocationFilter.SelectedIndex = CategoryFilter.SelectedIndex = TimeFilter.SelectedIndex = 0;
            ready = true;
            RefreshDashboard();
            RegistrationPanel.ReadingSaved += async (sender, e) => await ReloadAsync();
            refreshTimer.Tick += async (sender, e) => await ReloadAsync();
            IsVisibleChanged += async (sender, e) =>
            {
                if (IsVisible)
                {
                    refreshTimer.Start();
                    await ReloadAsync();
                }
                else refreshTimer.Stop();
            };
            Unloaded += (sender, e) => refreshTimer.Stop();
        }

        private async Task ReloadAsync()
        {
            if (fetching)
            {
                refreshPending = true;
                return;
            }
            fetching = true;
            RefreshButton.IsEnabled = false;
            ApiStatus.Text = "Fetching telemetry…";
            try
            {
                if (api == null) api = new TelemetryApiClient();
                var readings = await api.GetReadingsAsync();
                var packets = await api.GetPacketsAsync();
                readings.AddRange(packets.ChartReadings());
                var receivedAt = DateTime.Now;
                var next = readings.GroupBy(r => new { r.SensorId, r.Category }).Select(group =>
                {
                    var ordered = group.OrderBy(r => DateTimeOffset.Parse(r.Timestamp, CultureInfo.InvariantCulture)).ToList();
                    var latest = ordered.Last();
                    var lastTime = DateTimeOffset.Parse(latest.Timestamp, CultureInfo.InvariantCulture).LocalDateTime;
                    double previous = ordered.Count > 1 ? ordered[ordered.Count - 2].Value.Value : latest.Value.Value;
                    double change = latest.Value.Value - previous;
                    if (latest.Category == "Power") change = (new PowerReading((long)latest.Value.Value) - new PowerReading((long)previous)).Watts;
                    bool spike = ordered.Count > 1 && latest.Category != "Actuator" && Math.Abs(change) > (latest.Category == "Power" ? 500 : 20);
                    var sensor = new SensorSnapshot
                    {
                        Id = latest.SensorId,
                        Location = latest.Location,
                        Category = latest.Category,
                        LastReceived = lastTime,
                        Connected = receivedAt - lastTime <= TimeSpan.FromMinutes(5),
                        HasAlert = AboveThreshold(latest.Category, latest.Value.Value) || spike,
                        Spike = spike,
                        Change = change,
                        HasPrevious = ordered.Count > 1,
                        AlertTime = lastTime,
                        AlertValue = latest.Value.Value
                    };
                    sensor.Readings.AddRange(ordered.Select(r => new Reading
                    {
                        Time = DateTimeOffset.Parse(r.Timestamp, CultureInfo.InvariantCulture).LocalDateTime,
                        Value = r.Value.Value
                    }));
                    return sensor;
                }).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
                sensors.Clear();
                sensors.AddRange(next);
                snapshot = receivedAt;
                lastSynced = receivedAt;
                ready = false;
                UpdateFilter(SensorFilter, "All sensors", sensors.Select(s => s.Id));
                UpdateFilter(LocationFilter, "All locations", sensors.Select(s => s.Location));
                ready = true;
                DataMode.Text = "API CONNECTED";
                RefreshDashboard();
                DataMode.Text = "API CONNECTED";
                ApiStatus.Text = "Connected • " + readings.Count + " retained readings • Updated " +
                    receivedAt.ToString("HH:mm:ss") + (readings.Count == 0 ? " • Send a reading to get started." : "");
            }
            catch (Exception error)
            {
                DataMode.Text = "API OFFLINE";
                snapshot = DateTime.Now;
                RefreshDashboard();
                DataMode.Text = "API OFFLINE";
                ApiStatus.Text = "Cannot retrieve telemetry. " +
                    (lastSynced.HasValue ? "Showing cached data from " + lastSynced.Value.ToString("HH:mm:ss") + ". " : "No API data loaded. ") +
                    "Check that the API is running, then Refresh. " + FriendlyError(error);
            }
            finally
            {
                ready = true;
                fetching = false;
                RefreshButton.IsEnabled = true;
            }
            if (refreshPending)
            {
                refreshPending = false;
                await ReloadAsync();
            }
        }

        private static void UpdateFilter(ComboBox filter, string all, IEnumerable<string> values)
        {
            string selected = filter.SelectedItem as string;
            var options = new[] { all }.Concat(values.Distinct().OrderBy(v => v)).ToList();
            filter.ItemsSource = options;
            filter.SelectedItem = selected != null && options.Contains(selected) ? selected : all;
        }

        private static string FriendlyError(Exception error)
        {
            if (error is TaskCanceledException) return "The request timed out.";
            if (error is System.Net.Http.HttpRequestException) return "The API could not be reached.";
            return error.Message;
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await ReloadAsync();
        }

        private async void SendReading_Click(object sender, RoutedEventArgs e)
        {
            double value;
            if ((!double.TryParse(InputValue.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
                !double.TryParse(InputValue.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                SubmitStatus.Text = "Enter a valid numeric reading.";
                return;
            }
            if (string.IsNullOrWhiteSpace(InputSensor.Text) || string.IsNullOrWhiteSpace(InputLocation.Text))
            {
                SubmitStatus.Text = "Enter a sensor ID and location.";
                return;
            }
            SendReadingButton.IsEnabled = false;
            SubmitStatus.Text = "Sending reading…";
            try
            {
                if (api == null) api = new TelemetryApiClient();
                var saved = await api.SubmitAsync(new SubmitTelemetryRequest
                {
                    SensorId = InputSensor.Text.Trim(),
                    Location = InputLocation.Text.Trim(),
                    Category = (string)((ComboBoxItem)InputCategory.SelectedItem).Content,
                    Value = value,
                    Timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                });
                SubmitStatus.Text = "Saved " + saved.SensorId + " reading at " +
                    DateTimeOffset.Parse(saved.Timestamp, CultureInfo.InvariantCulture).ToLocalTime().ToString("HH:mm:ss") +
                    ". Reset filters if this sensor is hidden.";
                await ReloadAsync();
            }
            catch (Exception error)
            {
                SubmitStatus.Text = "Could not confirm the reading was saved. " + FriendlyError(error) +
                    " Refresh to check before retrying.";
            }
            finally
            {
                SendReadingButton.IsEnabled = true;
            }
        }
        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ready) RefreshDashboard();
        }

        private void RefreshDashboard()
        {
            CursorReadout.Text = "Hover over the chart to inspect the nearest reading.";
            int minutes = TimeFilter.SelectedIndex == 1 ? 30 : TimeFilter.SelectedIndex == 2 ? 5 : 60;
            rangeStart = snapshot.AddMinutes(-minutes);
            var matches = sensors.Where(s =>
                (SensorFilter.SelectedIndex == 0 || s.Id == (string)SensorFilter.SelectedItem) &&
                (LocationFilter.SelectedIndex == 0 || s.Location == (string)LocationFilter.SelectedItem) &&
                (CategoryFilter.SelectedIndex == 0 || s.Category == (string)CategoryFilter.SelectedItem)).ToList();

            foreach (var sensor in sensors) sensor.Connected = snapshot - sensor.LastReceived <= TimeSpan.FromMinutes(5);
            OnlineCount.Text = matches.Where(s => s.Connected).Select(s => s.Id).Distinct().Count().ToString();
            DisconnectedCount.Text = matches.Where(s => !s.Connected).Select(s => s.Id).Distinct().Count().ToString();
            var liveMeters = matches.Where(s => s.Category == "Power" && s.Connected).ToList();
            var totalPower = new PowerReading(0);
            foreach (var meter in liveMeters) totalPower += new PowerReading((long)meter.AlertValue);
            PowerSummary.Text = liveMeters.Count == 0 ? "Power total: no reporting meters in this selection." :
                "Combined power: " + totalPower.Watts + " W across " + liveMeters.Count + " reporting meter(s). Uses each meter's latest reading.";
            var activeAlerts = matches.Where(s => s.HasAlert && s.AlertTime >= rangeStart).ToList();
            AlertCount.Text = activeAlerts.Count.ToString();

            var cards = new List<AlertCard>();
            foreach (var sensor in activeAlerts.OrderByDescending(s => s.AlertTime))
            {
                cards.Add(new AlertCard
                {
                    SensorId = sensor.Id,
                    Category = sensor.Category,
                    Heading = "ALERT: Sensor " + sensor.Id + " • " + (sensor.Spike ? "sudden reading change" : "above threshold"),
                    Detail = "Location: " + sensor.Location + "     |     Value: " + sensor.AlertValue.ToString("0.##", CultureInfo.CurrentCulture) + " " + sensor.Unit +
                        "     |     " + sensor.AlertTime.ToString("HH:mm", CultureInfo.InvariantCulture) +
                        (sensor.HasPrevious ? " • Change: " + sensor.Change.ToString("+0.##;-0.##;0") + " " + sensor.Unit : "") +
                        "\n" + (sensor.Category == "Power" ? "Check the connected load and compare with the previous meter reading." : "Check sensor placement and wiring; compare the spike with neighbouring sensors."),
                    Accent = "#FFD044",
                    Icon = "⚠"
                });
            }
            // Place the disconnection next to the newest alert, as in the reference.
            foreach (var sensor in matches.Where(s => !s.Connected))
            {
                cards.Insert(Math.Min(1, cards.Count), new AlertCard
                {
                    SensorId = sensor.Id,
                    Category = sensor.Category,
                    Heading = sensor.Id + ": DISCONNECTED",
                    Detail = "Last received: " + sensor.LastReceived.ToString("HH:mm", CultureInfo.InvariantCulture) +
                        "     |     Location: " + sensor.Location + " • Last value: " + FormatValue(sensor.Category, sensor.AlertValue) +
                        "\nCheck device power, network signal and gateway connectivity. The last reading is retained for comparison.",
                    Accent = "#FF3038",
                    Icon = "●"
                });
            }
            AlertList.ItemsSource = cards;
            EmptyAlerts.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Plot one identified sensor so different units are never mixed into one series.
            chartSensor = matches.FirstOrDefault(s => s.Readings.Any(r => r.Time >= rangeStart));
            visibleReadings = chartSensor == null ? new List<Reading>() :
                chartSensor.Readings.Where(r => r.Time >= rangeStart).ToList();
            ChartCaption.Text = chartSensor == null ? "No sensor readings available" :
                chartSensor.Id + "  /  " + chartSensor.Location + "  /  " + chartSensor.Category +
                " (" + chartSensor.Unit + ")" + (matches.Count > 1 ? "  •  First matching sensor" : "");
            StartTime.Text = rangeStart.ToString("HH:mm", CultureInfo.InvariantCulture);
            EndTime.Text = snapshot.ToString("HH:mm", CultureInfo.InvariantCulture);
            InvestigationSummary.Text = chartSensor == null ? "Select an alert to investigate its sensor, or use the filters to explore readings." :
                "Inspecting " + chartSensor.Id + " • Latest: " + FormatValue(chartSensor.Category, chartSensor.AlertValue) +
                (chartSensor.HasPrevious && chartSensor.Category != "Actuator" ? " • Change from previous: " + chartSensor.Change.ToString("+0.##;-0.##;0") + " " + chartSensor.Unit : "") +
                " • " + (chartSensor.Connected ? "Reporting" : "Disconnected") +
                (DataMode.Text == "API OFFLINE" ? " • Cached data; API unavailable" : "");
            DrawChart();
        }

        private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ready) DrawChart();
        }

        private void DrawChart()
        {
            double width = Plot.ActualWidth;
            double height = Plot.ActualHeight;
            if (width < 2 || height < 2) return;
            double bottom = height - 1;
            double top = 25;
            double minimum = visibleReadings.Count == 0 ? 0 : Math.Min(0, Math.Floor(visibleReadings.Min(r => r.Value) / 25) * 25);
            bool actuator = chartSensor != null && chartSensor.Category == "Actuator";
            double maximum = visibleReadings.Count == 0 ? 125 : Math.Max(chartSensor.Category == "Power" ? 2500 : 125, Math.Ceiling((visibleReadings.Max(r => r.Value) + 1) / 25) * 25);
            if (actuator) { minimum = 0; maximum = 1; }
            double span = maximum - minimum;
            var axes = new StreamGeometry();
            using (var context = axes.Open())
            {
                context.BeginFigure(new Point(0.5, top), false, false);
                context.LineTo(new Point(0.5, bottom), true, false);
                context.LineTo(new Point(width, bottom), true, false);
            }
            ChartAxes.Data = axes;
            AxisLabels.Children.Clear();
            for (int step = 0; step <= (actuator ? 1 : 5); step++)
            {
                double tick = minimum + span * step / (actuator ? 1 : 5);
                var label = new TextBlock { Text = actuator ? (step == 0 ? "Off" : "On") : tick.ToString("0.#"), FontSize = 12, Foreground = Brushes.White };
                Canvas.SetRight(label, 14);
                Canvas.SetTop(label, bottom - (tick - minimum) / span * (bottom - top) - 8);
                AxisLabels.Children.Add(label);
            }

            var points = new PointCollection();
            foreach (var reading in visibleReadings)
            {
                var point = new Point(
                    8 + (reading.Time - rangeStart).TotalMinutes / (snapshot - rangeStart).TotalMinutes * (width - 128),
                    bottom - (reading.Value - minimum) / span * (bottom - top));
                if (actuator && points.Count > 0) points.Add(new Point(point.X, points[points.Count - 1].Y));
                points.Add(point);
            }
            ReadingLine.Points = points;
            LatestPoint.Visibility = points.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (points.Count > 0)
            {
                Canvas.SetLeft(LatestPoint, points[points.Count - 1].X - 3.5);
                Canvas.SetTop(LatestPoint, points[points.Count - 1].Y - 3.5);
            }
            ReadingLine.ToolTip = chartSensor == null || visibleReadings.Count == 0 ? null :
                chartSensor.Id + " • Latest: " + visibleReadings.Last().Value.ToString("0.0", CultureInfo.InvariantCulture) +
                " " + chartSensor.Unit + " at " + visibleReadings.Last().Time.ToString("HH:mm", CultureInfo.InvariantCulture);
            LatestPoint.ToolTip = ReadingLine.ToolTip;
            EmptyChart.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(EmptyChart, 24);
            Canvas.SetTop(EmptyChart, height / 2);
            double threshold = chartSensor != null && chartSensor.Category == "Power" ? 2000 : 75;
            double thresholdY = bottom - (threshold - minimum) / span * (bottom - top);
            ThresholdGuide.Visibility = chartSensor != null && !actuator ? Visibility.Visible : Visibility.Collapsed;
            ThresholdGuide.Data = new LineGeometry(new Point(0, thresholdY), new Point(width, thresholdY));
            ThresholdCaption.Text = chartSensor == null || actuator ? "Actuator chart: Off / On states" : "Threshold guide: " + threshold + " " + chartSensor.Unit;
            bool anomaly = chartSensor != null && points.Count > 0 && chartSensor.HasAlert;
            AnomalyMarker.Visibility = AnomalyLabel.Visibility = anomaly ? Visibility.Visible : Visibility.Collapsed;
            if (anomaly)
            {
                Point last = points[points.Count - 1];
                var marker = new StreamGeometry();
                using (var context = marker.Open())
                {
                    context.BeginFigure(new Point(last.X - 14, last.Y - 14), false, false);
                    context.LineTo(new Point(last.X + 14, last.Y + 14), true, false);
                    context.BeginFigure(new Point(last.X - 14, last.Y + 14), false, false);
                    context.LineTo(new Point(last.X + 14, last.Y - 14), true, false);
                }
                AnomalyMarker.Data = marker;
                Canvas.SetLeft(AnomalyLabel, last.X + 24);
                Canvas.SetTop(AnomalyLabel, last.Y - 9);
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            ready = false;
            SensorFilter.SelectedIndex = LocationFilter.SelectedIndex = CategoryFilter.SelectedIndex = TimeFilter.SelectedIndex = 0;
            ready = true;
            RefreshDashboard();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private static bool AboveThreshold(string category, double value)
        {
            if (category == "Actuator") return false;
            if (category == "Power") return new PowerReading((long)value) > new PowerReading(2000);
            return value > 75;
        }

        private static string FormatValue(string category, double value) => category == "Actuator" ? (value == 1 ? "On" : "Off") :
            value.ToString("0.##", CultureInfo.CurrentCulture) + (category == "Power" ? " W" : category == "Humidity" ? " %" : " °C");

        private void Investigate_Click(object sender, RoutedEventArgs e)
        {
            var alert = ((Button)sender).DataContext as AlertCard;
            if (alert == null) return;
            ready = false;
            LocationFilter.SelectedIndex = TimeFilter.SelectedIndex = 0;
            SensorFilter.SelectedItem = alert.SensorId;
            CategoryFilter.SelectedItem = alert.Category;
            ready = true;
            RefreshDashboard();
            ChartSection.BringIntoView();
        }

        private void Chart_MouseMove(object sender, MouseEventArgs e)
        {
            if (visibleReadings.Count == 0 || chartSensor == null) return;
            double fraction = Math.Max(0, Math.Min(1, (e.GetPosition(Plot).X - 8) / Math.Max(1, Plot.ActualWidth - 128)));
            var time = rangeStart.AddTicks((long)((snapshot - rangeStart).Ticks * fraction));
            var nearest = visibleReadings.OrderBy(r => Math.Abs((r.Time - time).Ticks)).First();
            CursorReadout.Text = chartSensor.Id + " • " + nearest.Time.ToString("HH:mm:ss") + " • " + FormatValue(chartSensor.Category, nearest.Value);
        }

        private sealed class SensorSnapshot
        {
            public string Id { get; set; }
            public string Location { get; set; }
            public string Category { get; set; }
            public string Unit => Category == "Temperature" ? "°C" : Category == "Power" ? "W" : Category == "Actuator" ? "state" : "%";
            public bool Spike { get; set; }
            public bool HasPrevious { get; set; }
            public double Change { get; set; }
            public bool Connected { get; set; }
            public bool HasAlert { get; set; }
            public DateTime AlertTime { get; set; }
            public double AlertValue { get; set; }
            public DateTime LastReceived { get; set; }
            public List<Reading> Readings { get; } = new List<Reading>();
        }

        private sealed class Reading
        {
            public DateTime Time { get; set; }
            public double Value { get; set; }
        }

        public sealed class AlertCard
        {
            public string SensorId { get; set; }
            public string Category { get; set; }
            public string Heading { get; set; }
            public string Detail { get; set; }
            public string Accent { get; set; }
            public string Icon { get; set; }
        }
    }
}


