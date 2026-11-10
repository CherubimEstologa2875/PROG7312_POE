# Smart-X IoT Gateway

# YOUTUBE DEMO LINK
https://youtu.be/EZ_6AoL5jGI 

Final Smart-X IoT mesh application for ST10443277 PROG7312. The solution combines the WPF gateway, ASP.NET Core API, telemetry and command processing, historical range queries, and mesh routing.

## Prerequisites

- Windows: the client is a WPF desktop application targeting .NET Framework 4.7.2.
- Visual Studio 2022 (17.14 or later) with the .NET desktop development and ASP.NET and web development workloads.
- .NET Framework 4.7.2 targeting pack/developer pack for compiling the client. In Visual Studio Installer, select the corresponding individual components if they are missing.
- .NET 9 SDK for compiling and running the backend, which targets `net9.0`. A runtime-only installation cannot compile the API.
- Internet access for the initial installation and dependency restore.

The projects currently use framework-provided dependencies and shared source files; no database server, API key, or separate NuGet package installation is required. The backend stores data in local JSON files and an attachment directory.

## 1. Open the project

Clone or extract the complete repository, including the `Shared` folder. Open Developer PowerShell for VS 2022 from the Windows Start menu so that both `MSBuild` and `dotnet` are available.

Change to the repository root: the directory containing this `README.md`, `tests`, and the outer `ST10443277_PROG7312` folder. Replace the example path with your actual download location:

```powershell
Set-Location 'C:\path\to\ST10443277_PROG7312_POE_PT1'
dotnet --list-sdks
MSBuild -version
```

Confirm that a `9.0.xxx` SDK is listed. All commands below assume this repository root as the working directory unless stated otherwise. Keep paths quoted if they contain spaces.

## 2. Restore dependencies

Restore the complete solution using Visual Studio's MSBuild:

```powershell
MSBuild .\ST10443277_PROG7312\ST10443277_PROG7312.sln /t:Restore
```

An “all projects are up-to-date” message is normal when the required restore assets already exist. Shared contracts are compiled into both projects automatically through their project files.

## 3. Compile the solution

Build both the API and WPF client in Debug configuration:

```powershell
MSBuild .\ST10443277_PROG7312\ST10443277_PROG7312.sln /t:Build /p:Configuration=Debug /verbosity:minimal
```

Wait for the build to finish successfully before starting either application. The outputs are:

- Backend: `ST10443277_PROG7312\SmartX.Api\bin\Debug\net9.0\SmartX.Api.dll`
- Client: `ST10443277_PROG7312\ST10443277_PROG7312\bin\Debug\ST10443277_PROG7312.exe`

Use Visual Studio's `MSBuild` for the complete solution because the client uses the classic .NET Framework WPF project format. To restore and build in one command, use:

```powershell
MSBuild .\ST10443277_PROG7312\ST10443277_PROG7312.sln /restore /t:Build /p:Configuration=Debug /verbosity:minimal
```

## 4. Boot the backend API

In the first terminal, run the API on the address expected by the client:

```powershell
dotnet run --project .\ST10443277_PROG7312\SmartX.Api\SmartX.Api.csproj --configuration Debug --no-build --no-launch-profile -- --urls http://localhost:5080
```

Keep this terminal open while using the client. Wait for the console to report `Now listening on: http://localhost:5080`.

The explicit URL and `--no-launch-profile` make this command use HTTP port 5080 consistently. The checked-in Visual Studio launch profile contains different ports (`54326` for HTTPS and `54327` for HTTP), while the client's default address is `http://localhost:5080/`.

### Swagger UI

The API includes an interactive OpenAPI page generated from its minimal API endpoints. With the API running locally, open:

```text
http://localhost:5080/swagger
```

The raw OpenAPI document is available at `http://localhost:5080/swagger/v1/swagger.json`.

### Run the API with Docker

Docker Desktop must be running. From the repository root, build and start the API with:

```powershell
docker compose up --build
```

The container maps its HTTP port to `http://localhost:5080`. Check `http://localhost:5080/health` or open Swagger UI at `http://localhost:5080/swagger`. The Compose configuration stores the API's JSON data in the named `smartx-data` volume, so the data remains available after the container is recreated.

To stop the API, press `Ctrl+C` or run:

```powershell
docker compose down
```

To stop it and intentionally delete the persisted Docker data as well, run `docker compose down -v`.

In a second PowerShell terminal, verify that the backend is ready:

```powershell
Invoke-RestMethod 'http://localhost:5080/health'
```

The response should contain `Status` with the value `Healthy`. You can also open [the health endpoint](http://localhost:5080/health) in a browser. The API's root URL does not serve the desktop interface; use `/health` to check availability.

## 5. Run the client user interface

In the second terminal, change to the same repository root and run:

```powershell
$env:SMARTX_API_URL = 'http://localhost:5080/'
Start-Process '.\ST10443277_PROG7312\ST10443277_PROG7312\bin\Debug\ST10443277_PROG7312.exe'
```

The Smart-X gateway window should appear. To try the implemented workflow:

1. Select Open telemetry on the gateway screen.
2. Enter a device ID, deployment location, and category, or select Simulate details to populate the form.
3. Select Register sensor to save the device. Simulating details alone does not save it.
4. Select its registry row to open the sensor profile, attach files, or submit a reading for that sensor.
5. Inspect the telemetry dashboard and filters. Select Refresh, or wait for the automatic 15-second refresh.

A new data directory starts with empty storage; register devices and submit readings to populate it. The gateway includes the telemetry, command-stream, and mesh-routing modules. Open Mesh routing to inspect the live graph, trace a registered sensor by MAC address or identifier, and review its last 24 hours of AVL-indexed telemetry history.

To stop the application, close the client window and press Ctrl+C in the backend terminal.

## Running the client from Visual Studio

1. Open `ST10443277_PROG7312\ST10443277_PROG7312.sln` in Visual Studio.
2. Right-click the solution and select Restore NuGet Packages, then select Build > Build Solution with the Debug / Any CPU configuration.
3. Start the backend in a separate terminal using the command in step 4 above.
4. Right-click the ST10443277_PROG7312 desktop project and select Set as Startup Project.
5. Press F5 to debug the client or Ctrl+F5 to run it without debugging. Keep the backend terminal running.

The client's `ST10443277_PROG7312\ST10443277_PROG7312\App.config` sets `TelemetryApiBaseUrl` to `http://localhost:5080/`. The environment variable `SMARTX_API_URL`, when present in the client process, takes precedence. If you change the server port, set this variable before launching the client, or update `App.config` and rebuild. Restart an already running client after changing its configuration.

## Local data storage

When launched using the documented `dotnet run` command, backend data is stored beneath `ST10443277_PROG7312\SmartX.Api\App_Data`:

- `telemetry.json`: submitted telemetry readings.
- `sensors.json`: registered sensors.
- `packets.json`: typed packet history.
- `attachments\index.json` and attachment files: uploaded files and their metadata.

Directories are created automatically; data files are written as records are saved and persist between runs. The application needs write access to this location. Stop the backend before backing up or restoring the directory, and keep the attachment index and its files together.

## Optional integration verification

After a successful Debug build, run the included feature suite from the repository root using Windows PowerShell 5.1 (`powershell.exe`):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\tests\FeatureIntegration.Tests.ps1
```

This suite runs `ApiIntegration.Tests.ps1` and `Part3Algorithms.Tests.ps1`, starts its own backend on port `5089`, and uses isolated storage under the API's `bin\integration` directory. Ensure that port is free. To select another test port, append `-Port 5091`. Windows PowerShell is required here because the tests load the .NET Framework WPF client assemblies.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| `dotnet` or `MSBuild` is not recognised | Install the prerequisites and reopen Developer PowerShell for VS 2022. |
| Visual Studio reports that it cannot target .NET 10 | This repository targets .NET 9 for Visual Studio 2022 compatibility. Install the .NET 9 SDK, reload the solution, and rebuild. |
| .NET Framework 4.7.2 reference assemblies are missing | Install the 4.7.2 targeting/developer pack through Visual Studio Installer, then rebuild. |
| The client cannot connect or displays cached data | Keep the API running, check `/health`, and confirm that `SMARTX_API_URL` or `TelemetryApiBaseUrl` matches the API's listening address. |
| Port 5080 is already in use | Stop the conflicting server you started, or choose another port in `--urls` and use the same address for the client. |
| A browser shows 404 at the API root | Check `/health`; launch the WPF executable for the user interface. |
| Build output files are locked | Close the running client and stop the backend, then rebuild. |
| The API reports invalid persisted data or missing attachments | Inspect the startup error and restore a consistent backup of the affected data. Startup validation rejects corrupt records instead of clearing them automatically. |

### Research engagement strategy

The telemetry dashboard combines interactive visualisation with proactive anomaly alerts and refreshes every 15 seconds. It uses a fixed five-minute disconnect timeout and sensor, location, category, and time filters. Updates are polled; immediate push updates, per-device reporting intervals, and a dedicated alert-status filter are not implemented.

- Threshold guides show the alert boundary on numeric charts; actuator charts use Off/On steps.
- Hover over the chart to inspect the nearest reading's value and timestamp.
- Alert cards show the sensor, location, actual value, change from the previous reading, and a contextual troubleshooting suggestion.
- Investigate sensor focuses the chart and filters on the alert's sensor and measurement type. Reset returns to the overall selection.
- The investigation summary reports the latest value, change, connection state, and whether cached data is being used.
- The power summary aggregates each reporting meter's latest value within the current filters. It excludes meters with readings older than five minutes. It is a latest-snapshot total, not a time-synchronised electrical measurement.

The command screen polls its bounded API queue and indexed device-state feed every five seconds. It supports device/location search, category filtering and active-alert-only filtering. Commands are persisted in `App_Data/commands.json`, remain queued until acknowledged, and are also retained as history. Boolean reports in telemetry remain telemetry; device actions use the separate command endpoint.

The real-time API processing path uses a FIFO `Queue<T>` for ordinary telemetry, a priority queue for critical power/moisture alerts, a `Stack<T>` for manual override undo, a device dictionary for O(1) live-state lookup, a `SortedDictionary` for timestamp-indexed timeline logs, and a `HashSet` for unique active alert/disconnect states. In the Commands screen the operator can now see actual timestamp-ordered reading rows, the total time range, and the count of unique active alert/disconnect states. The device dictionary remains directly usable through the device-ID lookup panel. HashSet membership represents active states keyed by type and device, not unique categories or dates; rebuilding it from current device states removes resolved/stale entries.

### Historical telemetry tree index

Historical telemetry is indexed per sensor by `TelemetryAvlTree` in `Shared/AdvancedStructures.cs`. It is a binary search tree ordered by `DateTimeOffset`; AVL rotations keep the tree balanced after every insert. `TelemetryStore.ReadRange(sensorId, from, to)` performs a pruned in-order range traversal, returning the matching readings in O(log n + k) time for k results. Both `GET /api/telemetry/range` and sensor-specific requests to `GET /api/telemetry` with `sensorId`, `from`, and `to` use this index. The tree exposes `Count`, `Height`, and `IsBalanced()` for verification.

### Mesh graph and routing algorithms

`MeshRouting.BuildCompleteGraph` models the gateway and ESP32 sensors as vertices with weighted wireless links. The weights are derived from node positions and represent relative hop cost. `MeshGraphTraversal.BreadthFirst` traverses the active online subgraph from the gateway, while `FindRecoveryHops` identifies online neighbours that can be used when a node is down. `MeshRouting.MinimumSpanningTree` applies Kruskal's algorithm with a union-find parent map to build the lowest-cost MST. A min-heap supports shortest-path processing, and selected routes are calculated only over the active online MST so failed nodes are not used as telemetry hops.

## Task 3 implementation reference

### Complete application workflow

The complete suite follows this sequence:

1. The operator starts the ASP.NET Core API and confirms `/health` returns `Healthy`.
2. The WPF client starts and reads the API URL from `SMARTX_API_URL` or `App.config`.
3. The operator opens Telemetry and registers sensors with identifiers, locations, and categories.
4. Telemetry readings are validated, persisted, indexed, and processed into latest-state, timeline, alert, and history views.
5. The operator uses filters and charts to investigate current readings and alerts.
6. The operator opens Commands to send, acknowledge, undo, and review device actions.
7. The operator opens Mesh routing to view the graph, BFS reachability, MST links, active routes, failed nodes, recovery hops, and AVL-indexed telemetry history.

The API is the system boundary. The WPF client does not write JSON files directly. This keeps validation, persistence, algorithms, and error handling in one backend that can later be replaced by a database-backed service without redesigning the UI.

### AVL tree historical index

Each sensor has a `TelemetryAvlTree` keyed by timestamp in `Shared/AdvancedStructures.cs`. An AVL tree is a binary search tree that rotates when its left and right subtree heights differ by more than one. This preserves logarithmic height even when readings arrive in sorted, reverse-sorted, or random timestamp order.

The insertion path is represented by the following implementation pattern:

```csharp
public void Add(DateTimeOffset key, TelemetryReading value)
{
    bool added = false;
    root = Insert(root, key, value, ref added);
    if (added) Count++;
}
```

The range query prunes branches outside the requested interval:

```csharp
public List<TelemetryReading> Range(DateTimeOffset from, DateTimeOffset to)
{
    var result = new List<TelemetryReading>();
    if (from > to) return result;
    VisitRange(root, from, to, result);
    return result;
}
```

`TelemetryStore.ReadRange` looks up the tree for one sensor and returns only matching timestamps. The complexity is O(log n + k), where n is the number of readings in the sensor index and k is the number of results. This is more efficient than filtering the entire retained list for every historical request.

### Graph model

The network graph is represented by `MeshNode` vertices and `MeshLink` edges. A node contains the sensor identifier, position, online state, and location. An edge contains its two endpoint identifiers and a weight. The weight is calculated from the simulated physical positions of the two nodes and acts as a relative wireless hop cost.

The graph construction uses a sparse ring and skip-link topology rather than generating every possible pair. This keeps the demonstration realistic and avoids unnecessary O(n²) edge generation when the gateway manages many sensors.

```csharp
var physicalGraph = MeshRouting.BuildCompleteGraph(nodes);
```

The name `BuildCompleteGraph` is retained for compatibility with the existing project, but the implementation deliberately builds a sparse mesh graph.

### BFS connected-device analysis

`MeshGraphTraversal.BreadthFirst` uses a queue. It starts at `gateway`, visits each reachable neighbour once, and records the order in which nodes are discovered. For network health, the API supplies a predicate so offline sensors are not treated as active hops:

```csharp
var reachable = MeshGraphTraversal.BreadthFirst(
    nodes,
    physicalGraph,
    "gateway",
    node => node.Gateway || node.Online);
```

This produces the active connected subgraph. `DownNodes` separately reports registered sensors whose latest state is offline. `FindRecoveryHops` examines the physical neighbours of each down sensor and lists online sensors or the gateway that could be used as recovery candidates.

BFS is appropriate here because the application needs to answer reachability questions rather than calculate a weighted optimum. Its graph traversal cost is O(V + E), where V is the number of vertices and E is the number of edges.

### Kruskal minimum spanning tree

`MeshRouting.MinimumSpanningTree` implements Kruskal's algorithm. It sorts graph edges by weight, then uses a union-find parent map to add the next cheapest edge only when it connects two different components:

```csharp
foreach (var link in links.OrderBy(l => l.Weight))
{
    string a = Root(parent, link.From);
    string b = Root(parent, link.To);
    if (a == b) continue;
    parent[a] = b;
    link.InSpanningTree = true;
}
```

The result connects the network with the lowest total edge cost while avoiding cycles. In Smart-X, this represents the power-efficient telemetry backbone. The API exposes the tree links and total `SpanningTreeCost` so the UI can display the MST overlay.

For a selected online sensor, the API builds an MST from the online subgraph and calculates the route to the gateway on that active tree. This prevents a failed sensor from being selected as a telemetry hop. The MST construction is O(E log E) because the edges are sorted before union-find processing.

### Min-heap route selection

The shared `MinHeap<T>` stores candidate paths ordered by their current cost. The shortest-path algorithm repeatedly removes the lowest-cost candidate and relaxes adjacent edges. This keeps route selection efficient and demonstrates how a heap supports graph algorithms.

### Verification checklist

Use the following checks when demonstrating or marking the implementation:

- Build the API and WPF client from the solution.
- Start the API and verify `/health`.
- Register at least two sensors and submit readings for them.
- Open Mesh routing and confirm the graph, MST count, and BFS count are populated.
- Select an online sensor and confirm an active gateway route is displayed.
- Allow a sensor to become stale or use an offline registration and confirm it appears under `DownNodes`.
- Confirm the route status identifies recovery hops for a down sensor.
- Select a sensor with history and confirm the mesh history panel reports an AVL tree range query.
- Run `FeatureIntegration.Tests.ps1` for the end-to-end API and WPF checks.

## Technology recommendations

The current local JSON design is appropriate for a demonstration and assessment environment. For a production Smart-X deployment, the following compatible additions are recommended:

| Recommendation | Benefit | .NET compatibility |
| --- | --- | --- |
| PostgreSQL | Durable multi-user telemetry storage, concurrent access, and indexed timestamp queries | .NET can connect through the Npgsql provider; check provider compatibility for the selected EF Core version |
| Google Cloud IoT-style architecture using Pub/Sub, Cloud Run, and Cloud SQL | Distributed ingestion, independently scalable API workers, and managed database hosting | .NET 10 services can run in containers on Cloud Run and consume Google client libraries or REST APIs |
| GitHub Actions | Automated build, API tests, WPF build checks, Docker image publishing, and release traceability | Directly runs `dotnet restore`, `dotnet build`, PowerShell tests, and Docker workflows |
| MQTT broker or Home Assistant integration | Standardised device messaging and interoperability with existing home/industrial automation devices | .NET MQTT client libraries can feed the existing telemetry endpoints |
| OpenTelemetry with Prometheus and Grafana | Operational metrics for API latency, queue depth, graph calculation time, and failed routes | Works with ASP.NET Core middleware and standard .NET instrumentation |
| LLM alert-summary service | Daily plain-language summaries of alerts, down nodes, and unusual telemetry patterns | Can be isolated behind a backend service so API keys never reach the WPF client |
| Redis or a distributed cache | Fast latest-state reads and short-lived route/summary caching across multiple API instances | Available through `Microsoft.Extensions.Caching.StackExchangeRedis` |

The recommended migration order is database persistence first, then CI/CD, then observability and device interoperability. An LLM should be added only after structured alert data and privacy controls are established, because generated summaries must not replace validated telemetry or safety rules.

The command dashboard also posts search/filter activity to the predictive action engine. The engine retains bounded query history, counts transitions between searches, combines those patterns with live threshold alerts, and presents confidence-scored Suggested Actions / Automated Insights. Available endpoints are `POST /api/insights/search` and `GET /api/insights`.

## Source layout

## Part 3 submission files

- `output/pdf/PROG7312_Part3_Final_POE_Report.pdf`: implementation report, completion report, feedback mapping, and technology recommendations.
- `PART3_UPDATE_LOG.md`: source and test update record, including the supplied Part 2 feedback and how it was addressed.
- This repository: complete WPF client, .NET 9 API, shared contracts, Docker files, and integration/algorithm test suites.
- Demo: the YouTube link at the top of this README.

Before submission, build and run the suite using the .NET 9 SDK, then commit and push the final changes to the configured GitHub repository. The target framework was changed to .NET 9 because the installed Visual Studio version cannot target .NET 10; confirm this change is acceptable against the course rubric before submission.

The paths below are relative to the outer `ST10443277_PROG7312` directory, except `tests`, which is at the repository root.

- `SmartX.Api/Program.cs`: minimal API endpoints and HTTP configuration.
- `SmartX.Api/Validation/TelemetryValidation.cs`: request and timestamp validation.
- `SmartX.Api/Stores/TelemetryStore.cs`: bounded, file-backed telemetry storage.
- `Shared/AdvancedStructures.cs`: AVL binary search tree for per-sensor timestamp range queries and mesh graph traversal.
- `Shared/MeshContracts.cs`: mesh graph, weighted edges, min-heap, MST, route and topology contracts.
- `SmartX.Api/Stores/SensorRegistrationStore.cs`: persistent sensor registry and duplicate protection.
- `SmartX.Api/Stores/AttachmentStore.cs` and `SmartX.Api/Endpoints/ProfileEndpoints.cs`: streamed file storage, profile endpoints and typed ingestion.
- `SmartX.Api/Stores/PacketStore.cs`: generic typed history ingestion and persistence.
- `SmartX.Api/Stores/CommandStore.cs`: bounded command queue, delivery state and persisted history.
- `SmartX.Api/Stores/TelemetryStore.cs`: bounded telemetry history plus AVL range indexes and indexed latest-state lookup for live device cards.
- `Shared/TelemetryStructures.cs`: generic packets, jagged arrays, typed histories and power operators.
- `Shared/ProfileContracts.cs`: attachment metadata and recursive deployment validation.
- `Shared/SensorRegistrationContracts.cs`: shared registration DTOs, validation and MAC normalization.
- `Shared/TelemetryContracts.cs`: request/response DTOs compiled into both projects.
- `Shared/CommandContracts.cs`: command request, status and history DTOs compiled into both projects.
- `ST10443277_PROG7312/TelemetryApiClient.cs`: asynchronous HTTP and JSON handling.
- `ST10443277_PROG7312/TelemetryDashboard.xaml` and code-behind: dashboard, submission form, refresh, filters and chart.
- `ST10443277_PROG7312/SensorRegistrationPanel.xaml` and code-behind: registration form, simulation, JSON import and registry table.
- `ST10443277_PROG7312/SensorProfilePanel.xaml` and code-behind: selected sensor files, transfers and typed reading submission.
- `ST10443277_PROG7312/MainWindow.xaml` and code-behind: gateway and navigation.
- `ST10443277_PROG7312/CommandStream.xaml` and code-behind: asynchronous command submission, polling and history.
- `tests/ApiIntegration.Tests.ps1`: API and WPF integration verification, including AVL range and mesh topology endpoints, visible sorted timeline, device dictionary lookup data, and unique active-state set.
- `tests/Part3Algorithms.Tests.ps1`: direct AVL balance/range, mesh traversal, recovery-hop, MST and route checks.
- `tests/FeatureIntegration.Tests.ps1`: runs the API integration and Part 3 algorithm checks, then verifies attachments, engagement and advanced C# concepts.

The multipart implementation follows Microsoft's [ASP.NET Core file upload guidance](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0), using streaming to avoid buffering whole uploads in application memory.
