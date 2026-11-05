using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartX.Contracts;

namespace ST10443277_PROG7312
{
    public partial class CommandStream : UserControl
    {
        private readonly TelemetryApiClient api = new TelemetryApiClient();
        private readonly ObservableCollection<CommandRow> rows = new ObservableCollection<CommandRow>();
        private readonly List<DeviceRow> devices = new List<DeviceRow>();
        private readonly ObservableCollection<InsightRow> insights = new ObservableCollection<InsightRow>();
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
                var insightsTask = api.GetInsightsAsync();
                await Task.WhenAll(historyTask, statesTask, insightsTask);
                var history = await historyTask;
                var states = await statesTask;
                var insightResponse = await insightsTask;
                rows.Clear();
                foreach (var command in history.Items) rows.Add(new CommandRow(command));
                devices.Clear();
                foreach (var state in states) devices.Add(new DeviceRow(state));
                ApplyDeviceFilter();
                insights.Clear();
                foreach (var suggestion in insightResponse.Suggestions) insights.Add(new InsightRow(suggestion));
                PatternSummaryText.Text = insightResponse.PatternSummary;
                PendingText.Text = history.Pending + " queued";
                StatusText.Text = "API CONNECTED • Updated " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception error) { StatusText.Text = "API OFFLINE • " + error.Message; }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(DeviceInput.Text)) { StatusText.Text = "Enter a target device."; return; }
            SendButton.IsEnabled = false;
            try
            {
                var selected = (ComboBoxItem)CommandInput.SelectedItem;
                await api.SubmitCommandAsync(new SubmitCommandRequest { DeviceId = DeviceInput.Text, Command = selected.Tag.ToString(), Parameters = ParametersInput.Text });
                ParametersInput.Clear(); StatusText.Text = "Command queued for " + DeviceInput.Text.Trim() + "."; await LoadAsync();
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
    }
}
