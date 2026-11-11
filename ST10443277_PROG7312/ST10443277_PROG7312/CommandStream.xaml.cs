using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using SmartX.Contracts;

namespace ST10443277_PROG7312
{
    public partial class CommandStream : UserControl
    {
        private readonly TelemetryApiClient api = new TelemetryApiClient();
        private readonly ObservableCollection<CommandRow> rows = new ObservableCollection<CommandRow>();
        private readonly List<DeviceRow> devices = new List<DeviceRow>();
        private readonly ObservableCollection<InsightRow> insights = new ObservableCollection<InsightRow>();
        private readonly ObservableCollection<TimelineRow> timelineRows = new ObservableCollection<TimelineRow>();
        private Dictionary<string, DeviceState> deviceDictionary = new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
        private ComboBox dictionaryInput;
        private List<SensorRegistration> registeredSensors = new List<SensorRegistration>();
        private TextBlock dictionaryResult;
        // Polling keeps the desktop view current while allowing the API to
        // remain independently deployable. The five-second interval is short
        // enough for the demo and avoids tying the UI to server-side events
        // (YouTube, [s.a.]-a; YouTube, [s.a.]-c).
        // Sources: https://youtu.be/A3ZUpyrnCbM; https://youtu.be/wHKlP8cov9w
        private readonly DispatcherTimer poller = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        public event EventHandler BackRequested;

        public CommandStream()
        {
            InitializeComponent();
            HistoryList.ItemsSource = rows;
            DeviceList.ItemsSource = devices;
            InsightsList.ItemsSource = insights;
            TimelineList.ItemsSource = timelineRows;
            AddDataStructureEvidencePanel();
            CommandInput.SelectedIndex = 0;
            CategoryFilter.SelectedIndex = 0;
            Loaded += async (s, e) => { poller.Tick += async (a, b) => await LoadAsync(); poller.Start(); await LoadAsync(); };
            Unloaded += (s, e) => poller.Stop();
        }

        private async Task LoadAsync()
        {
            try
            {
                // Fetch command history, live device state and insights in
                // parallel so one refresh updates all dashboard sections as a
                // single view (YouTube, [s.a.]-b).
                // Source: https://youtu.be/R94JHIXdTk0
                var historyTask = api.GetCommandsAsync();
                var statesTask = api.GetDeviceStatesAsync();
                var dictionaryTask = api.GetDeviceDictionaryAsync();
                var timelineTask = api.GetTimelineSummaryAsync();
                var timelineEntriesTask = api.GetTimelineAsync(12);
                var activeErrorsTask = api.GetActiveErrorStatesAsync();
                var insightsTask = api.GetInsightsAsync();
                var sensorsTask = api.GetSensorsAsync();
                await Task.WhenAll(historyTask, statesTask, dictionaryTask, timelineTask, timelineEntriesTask, activeErrorsTask, insightsTask, sensorsTask);
                var history = await historyTask;
                var states = await statesTask;
                deviceDictionary = await dictionaryTask;
                var timeline = await timelineTask;
                var timelineEntries = await timelineEntriesTask;
                var activeErrors = await activeErrorsTask;
                var insightResponse = await insightsTask;
                string selectedDeviceId = DeviceInput.SelectedValue as string;
                string selectedLookupId = dictionaryInput.SelectedValue as string;
                registeredSensors = await sensorsTask;
                DeviceInput.ItemsSource = registeredSensors;
                DeviceInput.SelectedValue = registeredSensors.Any(sensor => sensor.DeviceId == selectedDeviceId) ? selectedDeviceId : null;
                dictionaryInput.ItemsSource = registeredSensors;
                dictionaryInput.SelectedValue = registeredSensors.Any(sensor => sensor.DeviceId == selectedLookupId) ? selectedLookupId : null;
                rows.Clear();
                foreach (var command in history.Items) rows.Add(new CommandRow(command));
                devices.Clear();
                foreach (var state in states) devices.Add(new DeviceRow(state));
                ApplyDeviceFilter();
                insights.Clear();
                foreach (var suggestion in insightResponse.Suggestions) insights.Add(new InsightRow(suggestion));
                timelineRows.Clear();
                foreach (var reading in timelineEntries) timelineRows.Add(new TimelineRow(reading));
                PatternSummaryText.Text = insightResponse.PatternSummary;
                TimelineStatusText.Text = timelineEntries.Count == 0
                    ? "No readings in the sorted timeline yet."
                    : "Showing the newest " + timelineEntries.Count + " of " + timeline.ReadingCount + " readings, in chronological order. " +
                      "Range: " + FormatTimestamp(timelineEntries[0].Timestamp) + " to " + FormatTimestamp(timelineEntries[timelineEntries.Count - 1].Timestamp) + ".";
                UniqueErrorStateText.Text = "HashSet: " + activeErrors.Count + " unique active alert or disconnected-device state(s).";
                if (dictionaryResult != null)
                    dictionaryResult.Text = "Dictionary: " + deviceDictionary.Count + " device keys available. " +
                        "Sorted timeline: " + timeline.ReadingCount + " readings" +
                        (timeline.ReadingCount == 0 ? "." : " from " + FormatTimestamp(timeline.FirstTimestamp) + " to " + FormatTimestamp(timeline.LastTimestamp) + ".");
                PendingText.Text = history.Pending + " queued";
                StatusText.Text = "API CONNECTED • Updated " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception error) { StatusText.Text = "API OFFLINE • " + error.Message; }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevice = DeviceInput.SelectedValue as string;
            if (string.IsNullOrWhiteSpace(selectedDevice)) { StatusText.Text = "Choose a registered target device."; return; }
            SendButton.IsEnabled = false;
            try
            {
                var selected = (ComboBoxItem)CommandInput.SelectedItem;
                await api.SubmitCommandAsync(new SubmitCommandRequest { DeviceId = selectedDevice, Command = selected.Tag.ToString(), Parameters = ParametersInput.Text });
                ParametersInput.Clear(); StatusText.Text = "Command queued for " + selectedDevice + "."; await LoadAsync();
            }
            catch (Exception error) { StatusText.Text = "Command was not queued • " + error.Message; }
            finally { SendButton.IsEnabled = true; }
        }

        private async void Acknowledge_Click(object sender, RoutedEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as CommandRow; if (row == null) return;
            try { await api.AcknowledgeCommandAsync(row.Id); await LoadAsync(); } catch (Exception error) { StatusText.Text = "Could not acknowledge command • " + error.Message; }
        }
        private async void Refresh_Click(object sender, RoutedEventArgs e) { await LoadAsync(); }

        private void DictionaryLookup_Click(object sender, RoutedEventArgs e)
        {
            string key = (dictionaryInput.SelectedValue as string ?? "").Trim();
            DeviceState state;
            if (key.Length == 0) { dictionaryResult.Text = "Choose a registered device."; return; }
            if (!deviceDictionary.TryGetValue(key, out state))
            {
                dictionaryResult.Text = "No device found for key: " + key;
                return;
            }
            dictionaryResult.Text = "Found " + state.DeviceId + " • " + state.Location + " • " +
                (state.Online ? "ONLINE" : "OFFLINE") + " • latest value " + state.Value.ToString("0.##", CultureInfo.CurrentCulture);
        }

        private static string FormatTimestamp(string timestamp)
        {
            DateTimeOffset value;
            return DateTimeOffset.TryParse(timestamp, out value) ? value.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture) : timestamp;
        }

        private void AddDataStructureEvidencePanel()
        {
            var root = Content as Grid;
            if (root == null) return;
            root.RowDefinitions.Insert(3, new RowDefinition { Height = GridLength.Auto });
            foreach (UIElement child in root.Children)
            {
                if (Grid.GetRow(child) >= 3) Grid.SetRow(child, Grid.GetRow(child) + 1);
            }
            var panel = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 227, 235)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 16)
            };
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            var heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = "DEVICE DICTIONARY LOOKUP", FontSize = 16, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = "O(1) lookup by device ID • chronological index summary", Foreground = new SolidColorBrush(Color.FromRgb(87, 101, 121)), Margin = new Thickness(0, 4, 0, 0) });
            layout.Children.Add(heading);
            dictionaryInput = new ComboBox { Width = 190, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, DisplayMemberPath = "DeviceId", SelectedValuePath = "DeviceId", ToolTip = "Choose a registered device" };
            Grid.SetColumn(dictionaryInput, 1); layout.Children.Add(dictionaryInput);
            var actions = new StackPanel();
            var lookup = new Button { Content = "Lookup device", Padding = new Thickness(12, 7, 12, 7), Background = new SolidColorBrush(Color.FromRgb(18, 107, 96)), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            lookup.Click += DictionaryLookup_Click; actions.Children.Add(lookup);
            dictionaryResult = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(18, 107, 96)), Margin = new Thickness(0, 6, 0, 0) }; actions.Children.Add(dictionaryResult);
            Grid.SetColumn(actions, 2); layout.Children.Add(actions);
            panel.Child = layout; Grid.SetRow(panel, 3); root.Children.Add(panel);
        }
        private async void Undo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var undone = await api.UndoLastCommandAsync();
                StatusText.Text = undone == null ? "No reversible TurnOn/TurnOff override is available." : "Undo queued for " + undone.DeviceId + ".";
                await LoadAsync();
            }
            catch (Exception error) { StatusText.Text = "Undo failed • " + error.Message; }
        }
        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            ApplyDeviceFilter();
            if (IsLoaded)
            {
                try
                {
                    _ = api.RecordSearchAsync(new SearchActivity
                    {
                        Query = DeviceFilter.Text,
                        Category = CategoryFilter.SelectedIndex <= 0 ? "" : ((ComboBoxItem)CategoryFilter.SelectedItem).Content.ToString(),
                        AlertsOnly = AlertsOnly.IsChecked == true
                    });
                }
                catch { /* Filtering remains local if the API is temporarily unavailable. */ }
            }
        }
        private void ApplyDeviceFilter()
        {
            if (DeviceList == null) return;
            string search = (DeviceFilter.Text ?? "").Trim();
            string category = CategoryFilter.SelectedIndex <= 0 ? "" : ((ComboBoxItem)CategoryFilter.SelectedItem).Content.ToString();
            bool alerts = AlertsOnly.IsChecked == true;
            DeviceList.ItemsSource = devices.Where(d =>
                (search.Length == 0 || d.DeviceId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || d.Location.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (category.Length == 0 || d.Category == category) && (!alerts || d.ActiveAlert)).ToList();
            DeviceCountText.Text = DeviceList.Items.Count + " shown • " + devices.Count(d => d.Online) + " online";
        }
        private void Back_Click(object sender, RoutedEventArgs e) { BackRequested?.Invoke(this, EventArgs.Empty); }

        public sealed class CommandRow
        {
            private readonly DeviceCommand command;
            public CommandRow(DeviceCommand command) { this.command = command; }
            public string Id => command.Id;
            public string DeviceId => command.DeviceId;
            public string CommandSummary => command.Command + (string.IsNullOrWhiteSpace(command.Parameters) ? "" : " • " + command.Parameters);
            public string Status => command.Status;
            public string CreatedAtDisplay { get { DateTimeOffset value; return DateTimeOffset.TryParse(command.CreatedAt, out value) ? value.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture) : command.CreatedAt; } }
            public bool CanAcknowledge => command.Status == "Queued";
        }

        public sealed class DeviceRow
        {
            private readonly DeviceState state;
            public DeviceRow(DeviceState state) { this.state = state; }
            public string DeviceId { get { return state.DeviceId; } }
            public string Category { get { return state.Category; } }
            public bool ActiveAlert { get { return state.ActiveAlert; } }
            public bool Online { get { return state.Online; } }
            public string Location { get { return state.Location; } }
            public string StatusText { get { return state.ActiveAlert ? "ALERT" : state.Online ? "ONLINE" : "OFFLINE"; } }
            public string LocationCategory { get { return state.Location + " • " + state.Category; } }
            public string ValueText { get { return state.Category == "Actuator" ? (state.Value >= 1 ? "ON" : "OFF") : state.Value.ToString("0.##", CultureInfo.CurrentCulture) + (state.Category == "Power" ? " W" : state.Category == "Humidity" ? " %" : " °C"); } }
            public string LastSeenText { get { DateTimeOffset value; return DateTimeOffset.TryParse(state.LastSeen, out value) ? "Last seen " + value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "Last seen unknown"; } }
        }

        public sealed class InsightRow
        {
            private readonly SuggestedAction insight;
            public InsightRow(SuggestedAction insight) { this.insight = insight; }
            public string Title { get { return insight.Title; } }
            public string Explanation { get { return insight.Explanation + (string.IsNullOrWhiteSpace(insight.SuggestedCommand) ? "" : " Suggested command: " + insight.SuggestedCommand + "."); } }
            public string ConfidenceText { get { return insight.Confidence + "% confidence"; } }
        }

        public sealed class TimelineRow
        {
            public TimelineRow(TelemetryReading reading)
            {
                DateTimeOffset timestamp;
                Timestamp = DateTimeOffset.TryParse(reading.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp)
                    ? timestamp
                    : DateTimeOffset.MinValue;
                TimeText = Timestamp == DateTimeOffset.MinValue
                    ? reading.Timestamp
                    : Timestamp.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture);
                DeviceId = reading.SensorId;
                Category = reading.Category;
                ValueText = reading.Value.HasValue ? reading.Value.Value.ToString("0.##", CultureInfo.CurrentCulture) : "-";
            }

            public DateTimeOffset Timestamp { get; private set; }
            public string TimeText { get; private set; }
            public string DeviceId { get; private set; }
            public string Category { get; private set; }
            public string ValueText { get; private set; }
        }
    }
}
