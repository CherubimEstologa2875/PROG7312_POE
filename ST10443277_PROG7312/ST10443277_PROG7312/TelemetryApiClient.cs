using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SmartX.Contracts;

namespace ST10443277_PROG7312
{
    public sealed class TelemetryApiClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly HttpClient Transfers = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        private readonly Uri baseAddress;

        public TelemetryApiClient()
        {
            var address = Environment.GetEnvironmentVariable("SMARTX_API_URL") ??
                ConfigurationManager.AppSettings["TelemetryApiBaseUrl"] ?? "http://localhost:5080/";
            if (!Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out baseAddress) ||
                (baseAddress.Scheme != "http" && baseAddress.Scheme != "https"))
                throw new ConfigurationErrorsException("TelemetryApiBaseUrl must be an absolute HTTP or HTTPS address.");
        }

        public async Task<List<TelemetryReading>> GetReadingsAsync()
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/telemetry?limit=10000")).ConfigureAwait(false))
            {
                string json = await ReadResponseAsync(response).ConfigureAwait(false);
                return Serializer().Deserialize<List<TelemetryReading>>(json)
                    ?? throw new InvalidOperationException("The API returned an empty response.");
            }
        }

        public async Task<List<TelemetryReading>> GetTelemetryRangeAsync(string sensorId, DateTimeOffset from, DateTimeOffset to)
        {
            string path = "api/telemetry/range?sensorId=" + Uri.EscapeDataString(sensorId) +
                "&from=" + Uri.EscapeDataString(from.ToString("O")) +
                "&to=" + Uri.EscapeDataString(to.ToString("O"));
            using (var response = await Http.GetAsync(new Uri(baseAddress, path)).ConfigureAwait(false))
            {
                string json = await ReadResponseAsync(response).ConfigureAwait(false);
                return Serializer().Deserialize<List<TelemetryReading>>(json) ?? new List<TelemetryReading>();
            }
        }

        public async Task<CommandHistory> GetCommandsAsync()
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/commands")).ConfigureAwait(false))
                return Serializer().Deserialize<CommandHistory>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<List<DeviceState>> GetDeviceStatesAsync()
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/device-states")).ConfigureAwait(false))
                return Serializer().Deserialize<List<DeviceState>>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<Dictionary<string, DeviceState>> GetDeviceDictionaryAsync()
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/device-dictionary")).ConfigureAwait(false))
                return Serializer().Deserialize<Dictionary<string, DeviceState>>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<TimelineSummary> GetTimelineSummaryAsync()
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/telemetry/timeline/summary")).ConfigureAwait(false))
                return Serializer().Deserialize<TimelineSummary>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<MeshTopology> GetMeshAsync(string sensorId = null)
        {
            string path = "api/mesh" + (string.IsNullOrWhiteSpace(sensorId) ? "" : "?sensorId=" + Uri.EscapeDataString(sensorId));
            using (var response = await Http.GetAsync(new Uri(baseAddress, path)).ConfigureAwait(false))
                return Serializer().Deserialize<MeshTopology>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<DeviceCommand> SubmitCommandAsync(SubmitCommandRequest command)
        {
            using (var content = new StringContent(Serializer().Serialize(command), Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(new Uri(baseAddress, "api/commands"), content).ConfigureAwait(false))
                return Serializer().Deserialize<DeviceCommand>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<DeviceCommand> AcknowledgeCommandAsync(string id)
        {
            using (var response = await Http.PostAsync(new Uri(baseAddress, "api/commands/" + Uri.EscapeDataString(id) + "/acknowledge"), null).ConfigureAwait(false))
                return Serializer().Deserialize<DeviceCommand>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<DeviceCommand> UndoLastCommandAsync()
        {
            using (var response = await Http.PostAsync(new Uri(baseAddress, "api/commands/undo"), null).ConfigureAwait(false))
                return Serializer().Deserialize<DeviceCommand>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<InsightResponse> GetInsightsAsync()
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/insights")).ConfigureAwait(false))
                return Serializer().Deserialize<InsightResponse>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task RecordSearchAsync(SearchActivity activity)
        {
            using (var content = new StringContent(Serializer().Serialize(activity), Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(new Uri(baseAddress, "api/insights/search"), content).ConfigureAwait(false))
                await ReadResponseAsync(response).ConfigureAwait(false);
        }

        public async Task<TelemetryReading> SubmitAsync(SubmitTelemetryRequest reading)
        {
            using (var content = new StringContent(Serializer().Serialize(reading), Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(new Uri(baseAddress, "api/telemetry"), content).ConfigureAwait(false))
            {
                string json = await ReadResponseAsync(response).ConfigureAwait(false);
                return Serializer().Deserialize<TelemetryReading>(json)
                    ?? throw new InvalidOperationException("The API did not return the saved reading.");
            }
        }

        public async Task<List<SensorRegistration>> GetSensorsAsync()
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/sensors")).ConfigureAwait(false))
            {
                string json = await ReadResponseAsync(response).ConfigureAwait(false);
                return Serializer().Deserialize<List<SensorRegistration>>(json)
                    ?? throw new InvalidOperationException("The API returned an empty sensor response.");
            }
        }

        public async Task<SensorRegistration> RegisterSensorAsync(RegisterSensorRequest sensor)
        {
            using (var content = new StringContent(Serializer().Serialize(sensor), Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(new Uri(baseAddress, "api/sensors"), content).ConfigureAwait(false))
            {
                string json = await ReadResponseAsync(response).ConfigureAwait(false);
                return Serializer().Deserialize<SensorRegistration>(json)
                    ?? throw new InvalidOperationException("The API did not return the saved registration.");
            }
        }

        public async Task<PacketHistory> GetPacketsAsync()
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/packets")).ConfigureAwait(false))
                return Serializer().Deserialize<PacketHistory>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<TelemetryPacket<T>> SubmitPacketAsync<T>(string kind, TelemetryPacket<T> packet) where T : struct
        {
            using (var content = new StringContent(Serializer().Serialize(packet), Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(new Uri(baseAddress, "api/packets/" + Uri.EscapeDataString(kind)), content).ConfigureAwait(false))
                return Serializer().Deserialize<TelemetryPacket<T>>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<List<SensorAttachment>> GetAttachmentsAsync(string sensorId)
        {
            using (var response = await Http.GetAsync(new Uri(baseAddress, "api/sensors/" + Uri.EscapeDataString(sensorId) + "/attachments")).ConfigureAwait(false))
                return Serializer().Deserialize<List<SensorAttachment>>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        public async Task<SensorAttachment> UploadAttachmentAsync(string sensorId, string kind, string path, CancellationToken cancellation)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                if (stream.Length == 0 || stream.Length > 10 * 1024 * 1024) throw new InvalidOperationException("Choose a non-empty file up to 10 MB.");
                using (var multipart = new MultipartFormDataContent())
                {
                    multipart.Add(new StreamContent(stream, 65536), "file", Path.GetFileName(path));
                    using (var response = await Transfers.PostAsync(new Uri(baseAddress, "api/sensors/" + Uri.EscapeDataString(sensorId) +
                        "/attachments?kind=" + Uri.EscapeDataString(kind)), multipart, cancellation).ConfigureAwait(false))
                        return Serializer().Deserialize<SensorAttachment>(await ReadResponseAsync(response).ConfigureAwait(false));
                }
            }
        }

        public async Task DownloadAttachmentAsync(string sensorId, string attachmentId, string destination, CancellationToken cancellation)
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using (var response = await Transfers.GetAsync(new Uri(baseAddress, "api/sensors/" + Uri.EscapeDataString(sensorId) +
                    "/attachments/" + Uri.EscapeDataString(attachmentId)), HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) await ReadResponseAsync(response).ConfigureAwait(false);
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        await input.CopyToAsync(output, 65536, cancellation).ConfigureAwait(false);
                }
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public async Task<DeploymentValidationResult> ValidateDeploymentAsync(string sensorId, DeploymentNode tree)
        {
            using (var content = new StringContent(Serializer().Serialize(tree), Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(new Uri(baseAddress, "api/sensors/" + Uri.EscapeDataString(sensorId) + "/deployment/validate"), content).ConfigureAwait(false))
                return Serializer().Deserialize<DeploymentValidationResult>(await ReadResponseAsync(response).ConfigureAwait(false));
        }

        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        }

        private static async Task<string> ReadResponseAsync(HttpResponseMessage response)
        {
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return json;
            string detail = "API request failed (" + (int)response.StatusCode + ").";
            try
            {
                var problem = Serializer().Deserialize<Dictionary<string, object>>(json);
                if (problem.ContainsKey("detail")) detail = Convert.ToString(problem["detail"]);
                else if (problem.ContainsKey("errors"))
                {
                    var errors = problem["errors"] as Dictionary<string, object>;
                    if (errors != null)
                    {
                        var messages = new List<string>();
                        foreach (var error in errors)
                        {
                            var values = error.Value as System.Collections.IEnumerable;
                            if (values != null)
                                foreach (var value in values) messages.Add(error.Key + ": " + value);
                        }
                        detail = string.Join(" ", messages);
                    }
                }
            }
            catch (ArgumentException) { /* Non-JSON server responses retain their HTTP status. */ }
            throw new InvalidOperationException(detail);
        }
    }
}
