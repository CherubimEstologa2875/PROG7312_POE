using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SmartX.Contracts;

namespace ST10443277_PROG7312
{
    public partial class SensorRegistrationPanel : UserControl
    {
        private readonly ObservableCollection<SensorRegistration> records = new ObservableCollection<SensorRegistration>();
        private TelemetryApiClient api;
        private bool busy;
        private bool hasSnapshot;
        public event EventHandler ReadingSaved;

        private async void ProfileSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ProfilePanel != null) await ProfilePanel.SelectSensorAsync(RegistryGrid.SelectedItem as SensorRegistration);
        }

        private void ProfileReadingSaved(object sender, EventArgs e) { ReadingSaved?.Invoke(this, EventArgs.Empty); }

        public SensorRegistrationPanel()
        {
            InitializeComponent();
            CategoryInput.ItemsSource = SensorRegistrationRules.Categories;
            RegistryGrid.ItemsSource = records;
            IsVisibleChanged += async (sender, args) =>
            {
                if (IsVisible) await RefreshRegistryAsync();
            };
        }

        private void SetBusy(bool value)
        {
            busy = value;
            DraftFields.IsEnabled = RefreshRegistryButton.IsEnabled = !value;
        }

        private void ClearErrors()
        {
            DeviceIdError.Text = LocationError.Text = CategoryError.Text = "";
        }

        private async void Register_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            ClearErrors();
            var request = new RegisterSensorRequest
            {
                DeviceId = DeviceIdInput.Text,
                Location = LocationInput.Text,
                Category = CategoryInput.SelectedItem as string
            };
            var errors = SensorRegistrationRules.Validate(request);
            if (errors.Count > 0)
            {
                DeviceIdError.Text = errors.ContainsKey("DeviceId") ? errors["DeviceId"][0] : "";
                LocationError.Text = errors.ContainsKey("Location") ? errors["Location"][0] : "";
                CategoryError.Text = errors.ContainsKey("Category") ? errors["Category"][0] : "";
                FormStatus.Text = "Correct the highlighted fields before registering.";
                if (errors.ContainsKey("DeviceId")) DeviceIdInput.Focus();
                else if (errors.ContainsKey("Location")) LocationInput.Focus();
                else CategoryInput.Focus();
                return;
            }

            SetBusy(true);
            FormStatus.Text = "Saving sensor registration…";
            try
            {
                if (api == null) api = new TelemetryApiClient();
                var saved = await api.RegisterSensorAsync(request);
                // Show the confirmed write even if the subsequent registry fetch is unavailable.
                records.Add(saved);
                RegistryCount.Text = "Registered sensors (" + records.Count + ")";
                DeviceIdInput.Clear();
                LocationInput.Clear();
                CategoryInput.SelectedIndex = -1;
                FormStatus.Text = "Registered " + saved.DeviceId + " at " + saved.Location + " (" + saved.Category + ").";
                await LoadRegistryAsync();
            }
            catch (Exception error)
            {
                FormStatus.Text = "Registration could not be confirmed. " + FriendlyError(error) +
                    " Your details have been kept. Refresh the registry before retrying.";
            }
            finally { SetBusy(false); }
        }

        private async Task LoadRegistryAsync()
        {
            RegistryStatus.Text = "Loading registered sensors…";
            try
            {
                if (api == null) api = new TelemetryApiClient();
                var loaded = await api.GetSensorsAsync();
                string selectedId = (RegistryGrid.SelectedItem as SensorRegistration)?.Id;
                records.Clear();
                foreach (var sensor in loaded) records.Add(sensor);
                RegistryGrid.SelectedItem = records.FirstOrDefault(s => s.Id == selectedId);
                hasSnapshot = true;
                RegistryCount.Text = "Registered sensors (" + records.Count + ")";
                RegistryStatus.Text = records.Count == 0 ? "No sensors registered yet. Add your first device above." :
                    "Registry updated at " + DateTime.Now.ToString("HH:mm:ss") + ". Records are saved by the API.";
            }
            catch (Exception error)
            {
                RegistryStatus.Text = "Registry unavailable. " + FriendlyError(error) +
                    (hasSnapshot || records.Count > 0 ? " Showing previously retrieved or confirmed records; this list may be incomplete." : " Start the API and select Refresh registry.");
            }
        }

        private async Task RefreshRegistryAsync()
        {
            if (busy) return;
            SetBusy(true);
            try { await LoadRegistryAsync(); }
            finally { SetBusy(false); }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) { await RefreshRegistryAsync(); }

        private void Simulate_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            ClearErrors();
            byte[] bytes = Guid.NewGuid().ToByteArray();
            // A locally administered unicast MAC is appropriate for a simulated device.
            bytes[0] = (byte)((bytes[0] | 2) & 254);
            DeviceIdInput.Text = string.Join(":", bytes.Take(6).Select(b => b.ToString("X2")));
            LocationInput.Text = "Greenhouse / Zone " + (char)('A' + bytes[6] % 4) + " / Node " + (bytes[7] % 50 + 1);
            CategoryInput.SelectedIndex = bytes[8] % SensorRegistrationRules.Categories.Length;
            FormStatus.Text = "Simulated details are ready. Review or edit them, then select Register sensor to save.";
        }

        // Loading a file prepares a draft only; invalid imports never overwrite the current draft.
        public void LoadDraftFromJson(string json)
        {
            if (busy) throw new InvalidOperationException("Wait for the current registry request to finish.");
            if (json == null || Encoding.UTF8.GetByteCount(json) > 16 * 1024)
                throw new InvalidDataException("The JSON record must be no larger than 16 KB.");
            var fields = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
            if (fields == null || new[] { "DeviceId", "Location", "Category" }.Any(key => !fields.ContainsKey(key) || !(fields[key] is string)))
                throw new InvalidDataException("Use one JSON object with string fields DeviceId, Location and Category.");
            var draft = new RegisterSensorRequest
            {
                DeviceId = (string)fields["DeviceId"], Location = (string)fields["Location"], Category = (string)fields["Category"]
            };
            var errors = SensorRegistrationRules.Validate(draft);
            if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors.Values.SelectMany(v => v)));
            ClearErrors();
            DeviceIdInput.Text = draft.DeviceId;
            LocationInput.Text = draft.Location;
            CategoryInput.SelectedItem = draft.Category;
            FormStatus.Text = "JSON record loaded. Review the details, then select Register sensor to save.";
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var dialog = new OpenFileDialog { Title = "Load a sensor registration record", Filter = "JSON sensor record (*.json)|*.json", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog() != true) return;
            try
            {
                using (var stream = File.OpenRead(dialog.FileName))
                {
                    if (stream.Length > 16 * 1024) throw new InvalidDataException("The JSON record must be no larger than 16 KB.");
                    using (var reader = new StreamReader(stream)) LoadDraftFromJson(reader.ReadToEnd());
                }
            }
            catch (Exception error) { FormStatus.Text = "Could not load the record. " + error.Message; }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            ClearErrors();
            DeviceIdInput.Clear();
            LocationInput.Clear();
            CategoryInput.SelectedIndex = -1;
            FormStatus.Text = "Form cleared. Enter details, simulate a device or load a JSON record.";
            DeviceIdInput.Focus();
        }

        private static string FriendlyError(Exception error)
        {
            if (error is TaskCanceledException) return "The request timed out.";
            if (error is System.Net.Http.HttpRequestException) return "The API could not be reached.";
            return error.Message;
        }
    }
}
