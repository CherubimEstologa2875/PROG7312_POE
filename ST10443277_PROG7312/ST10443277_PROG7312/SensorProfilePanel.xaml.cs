using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SmartX.Contracts;

namespace ST10443277_PROG7312
{
    public partial class SensorProfilePanel : UserControl
    {
        private SensorRegistration sensor;
        private TelemetryApiClient api;
        private CancellationTokenSource transfer;
        private bool busy;
        private int selectionVersion;
        public event EventHandler ReadingSaved;

        public SensorProfilePanel() { InitializeComponent(); }

        public async Task SelectSensorAsync(SensorRegistration selected)
        {
            selectionVersion++;
            sensor = selected;
            AttachmentList.ItemsSource = null;
            ProfileStatus.Text = FileStatus.Text = "";
            ProfileHeading.Text = sensor == null ? "Select a registered sensor above to manage its files and readings." :
                sensor.DeviceId + " • " + sensor.Location + " • " + sensor.Category;
            ProfileActions.IsEnabled = sensor != null && !busy;
            ProfileActions.Visibility = sensor == null ? Visibility.Collapsed : Visibility.Visible;
            PacketValue.Text = "";
            SwitchValue.SelectedIndex = 0;
            bool actuator = sensor != null && sensor.Category == "Actuator";
            SwitchValue.Visibility = actuator ? Visibility.Visible : Visibility.Collapsed;
            PacketValue.Visibility = actuator ? Visibility.Collapsed : Visibility.Visible;
            ValueHint.Text = actuator ? "Report the observed on/off state. This sends telemetry; it does not command hardware." :
                sensor != null && sensor.Category == "Power Consumption" ? "Power consumption in whole watts (0–2,147,483,647)." : "Temperature in °C (-273.15–10000).";
            if (sensor != null) await LoadFilesAsync(sensor.Id, selectionVersion);
        }

        private async Task LoadFilesAsync(string id, int version)
        {
            try
            {
                if (api == null) api = new TelemetryApiClient();
                var files = await api.GetAttachmentsAsync(id);
                if (version != selectionVersion) return;
                AttachmentList.ItemsSource = files;
                FileStatus.Text = files.Count == 0 ? "No files attached to this sensor yet." : files.Count + " file(s) attached to this sensor.";
            }
            catch (Exception error) { if (version == selectionVersion) FileStatus.Text = "Could not refresh files. " + error.Message; }
        }

        private async void RefreshFiles_Click(object sender, RoutedEventArgs e)
        {
            if (sensor != null && !busy) await LoadFilesAsync(sensor.Id, selectionVersion);
        }

        private void SetBusy(bool value)
        {
            busy = value;
            ProfileActions.IsEnabled = !value && sensor != null;
        }

        public async Task UploadFileAsync(string path, string kind)
        {
            if (sensor == null || busy) return;
            var selected = sensor;
            int version = selectionVersion;
            SetBusy(true);
            transfer = new CancellationTokenSource();
            CancelTransferButton.Visibility = Visibility.Visible;
            ProfileStatus.Text = "Uploading " + Path.GetFileName(path) + " for " + selected.DeviceId + "…";
            try
            {
                if (api == null) api = new TelemetryApiClient();
                var saved = await api.UploadAttachmentAsync(selected.Id, kind, path, transfer.Token);
                if (version == selectionVersion)
                {
                    ProfileStatus.Text = "Attached " + saved.FileName + " to " + selected.DeviceId + ".";
                    await LoadFilesAsync(selected.Id, version);
                }
            }
            catch (Exception error)
            {
                if (version == selectionVersion) ProfileStatus.Text = (error is OperationCanceledException ? "Transfer cancelled or timed out." : "Upload was not confirmed. " + error.Message) + " Refresh files before retrying.";
            }
            finally
            {
                transfer.Dispose(); transfer = null;
                CancelTransferButton.Visibility = Visibility.Collapsed;
                SetBusy(false);
            }
        }

        private async void Upload_Click(object sender, RoutedEventArgs e)
        {
            if (sensor == null || busy) return;
            string kind = (string)((ComboBoxItem)AttachmentKind.SelectedItem).Content;
            string filter = kind == "Photo" ? "Deployment photo|*.png;*.jpg;*.jpeg" : kind == "Log" ? "Hardware logs|*.log;*.txt;*.csv" :
                kind == "Deployment" ? "Deployment tree|*.json" : "Device configuration|*.json;*.xml;*.ini;*.cfg;*.conf;*.yaml;*.yml";
            var dialog = new OpenFileDialog { Title = "Attach " + kind.ToLowerInvariant() + " to " + sensor.DeviceId, Filter = filter, Multiselect = false };
            if (dialog.ShowDialog() == true) await UploadFileAsync(dialog.FileName, kind);
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            var file = AttachmentList.SelectedItem as SensorAttachment;
            if (sensor == null || file == null || busy) { ProfileStatus.Text = "Select an attached file first."; return; }
            var selected = sensor;
            int version = selectionVersion;
            var dialog = new SaveFileDialog { FileName = file.FileName, Filter = "All files|*.*", OverwritePrompt = true };
            if (dialog.ShowDialog() != true) return;
            SetBusy(true);
            transfer = new CancellationTokenSource();
            CancelTransferButton.Visibility = Visibility.Visible;
            try
            {
                if (api == null) api = new TelemetryApiClient();
                await api.DownloadAttachmentAsync(selected.Id, file.Id, dialog.FileName, transfer.Token);
                if (version == selectionVersion) ProfileStatus.Text = "Saved " + file.FileName + ".";
            }
            catch (Exception error) { if (version == selectionVersion) ProfileStatus.Text = "Download did not complete. " + error.Message; }
            finally { transfer.Dispose(); transfer = null; CancelTransferButton.Visibility = Visibility.Collapsed; SetBusy(false); }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { transfer?.Cancel(); }

        private TelemetryPacket<T> Packet<T>(T value) where T : struct => new TelemetryPacket<T>
        {
            SensorId = sensor.DeviceId, Location = sensor.Location, Timestamp = DateTimeOffset.UtcNow.ToString("O"), Value = value
        };

        private async void SendPacket_Click(object sender, RoutedEventArgs e)
        {
            if (sensor == null || busy) return;
            int version = selectionVersion;
            SetBusy(true);
            try
            {
                if (api == null) api = new TelemetryApiClient();
                if (sensor.Category == "Actuator") await api.SubmitPacketAsync("actuator", Packet(SwitchValue.SelectedIndex == 1));
                else if (sensor.Category == "Power Consumption")
                {
                    if (!int.TryParse(PacketValue.Text, out int watts) || watts < 0) throw new InvalidOperationException("Enter a non-negative whole number of watts.");
                    await api.SubmitPacketAsync("power", Packet(watts));
                }
                else
                {
                    if ((!float.TryParse(PacketValue.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float temperature) &&
                        !float.TryParse(PacketValue.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature)) ||
                        float.IsNaN(temperature) || float.IsInfinity(temperature)) throw new InvalidOperationException("Enter a finite temperature in °C.");
                    await api.SubmitPacketAsync("temperature", Packet(temperature));
                }
                if (version == selectionVersion) ProfileStatus.Text = "Profile reading saved. The telemetry dashboard is refreshing.";
                ReadingSaved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception error) { if (version == selectionVersion) ProfileStatus.Text = "Reading was not confirmed. " + error.Message + " Refresh telemetry before retrying."; }
            finally { SetBusy(false); }
        }
    }
}
