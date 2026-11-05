param([int]$Port = 5087)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http, PresentationFramework, PresentationCore, WindowsBase
[System.Threading.SynchronizationContext]::SetSynchronizationContext([System.Windows.Threading.DispatcherSynchronizationContext]::new([System.Windows.Threading.Dispatcher]::CurrentDispatcher))
$repo = Split-Path $PSScriptRoot -Parent
$apiRoot = Join-Path $repo 'ST10443277_PROG7312/SmartX.Api'
$apiDll = Join-Path $apiRoot 'bin/Debug/net9.0/SmartX.Api.dll'
$clientDll = Join-Path $repo 'ST10443277_PROG7312/ST10443277_PROG7312/bin/Debug/ST10443277_PROG7312.exe'
$testDirectory = Join-Path $apiRoot ('bin/integration/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$dataFile = Join-Path $testDirectory 'telemetry.json'
$registrationFile = Join-Path $testDirectory 'sensors.json'
$packetFile = Join-Path $testDirectory 'packets.json'
$attachmentRoot = Join-Path $testDirectory 'attachments'
# Refuse an occupied port so tests cannot accidentally post to another API instance.
$portCheck = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
try { $portCheck.Start() } finally { $portCheck.Stop() }
$baseUri = "http://127.0.0.1:$Port"
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(5)
$previousApiUrl = $env:SMARTX_API_URL
$env:SMARTX_API_URL = $baseUri
$server = $null
function Assert($condition, $message) { if (!$condition) { throw $message } }
function Request($method, $path, $body = $null) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), "$baseUri$path")
    if ($null -ne $body) {
        $json = if ($body -is [string]) { $body } else { ConvertTo-Json $body -Compress }
        $request.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    }
    try {
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $result = $null
            if ($text) { try { $result = ConvertFrom-Json $text } catch {} }
            return @{ Code = [int]$response.StatusCode; Body = $result; Location = $response.Headers.Location }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function Start-Api {
    $script:server = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @(
        ('"' + $apiDll + '"'), '--urls', $baseUri, ('"--Telemetry:DataFile=' + $dataFile + '"'), ('"--Registration:DataFile=' + $registrationFile + '"'),
        ('"--Packets:DataFile=' + $packetFile + '"'), ('"--Attachments:Root=' + $attachmentRoot + '"')
    ) -WorkingDirectory $apiRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testDirectory 'api.stdout.log') -RedirectStandardError (Join-Path $testDirectory 'api.stderr.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:server.HasExited) { throw 'API exited unexpectedly; inspect test logs.' }
        try {
            if ((Request GET '/health').Code -eq 200) { return }
        } catch {}
        Start-Sleep -Milliseconds 100
    }
    throw 'API did not become healthy.'
}
function Stop-Api {
    if ($script:server -and !$script:server.HasExited) {
        Stop-Process -Id $script:server.Id
        $script:server.WaitForExit()
    }
}
function Reading($id, $value, $minutes = -1, $category = 'Temperature') {
    return @{
        SensorId = $id; Location = 'Zone A'; Category = $category; Value = $value
        Timestamp = [DateTimeOffset]::UtcNow.AddMinutes($minutes).ToString('O')
    }
}
function Pump-Until($condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    while (!( & $condition )) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Timed out waiting for dashboard action.' }
        $frame = New-Object System.Windows.Threading.DispatcherFrame
        $timer = New-Object System.Windows.Threading.DispatcherTimer
        $timer.Interval = [TimeSpan]::FromMilliseconds(20)
        $timer.Add_Tick({ $frame.Continue = $false })
        $timer.Start()
        [System.Windows.Threading.Dispatcher]::PushFrame($frame)
        $timer.Stop()
    }
}
function Click($control) { $control.RaiseEvent([System.Windows.RoutedEventArgs]::new([System.Windows.Controls.Button]::ClickEvent)) }
function Control($name) { $dashboard.FindName($name) }
try {
    Start-Api
    Assert ((Request GET '/api/sensors').Body.Count -eq 0) 'Registry must start empty.'
    $registered = Request POST '/api/sensors' @{ DeviceId = ' aa-bb-cc-dd-ee-01 '; Location = ' Zone B / Node 3 '; Category = 'environmental' }
    Assert ($registered.Code -eq 201) 'Registration must return 201.'
    Assert ($registered.Body.DeviceId -ceq 'AA:BB:CC:DD:EE:01') 'MAC must be normalized.'
    Assert ($registered.Body.Location -ceq 'Zone B / Node 3') 'Location must be trimmed.'
    Assert ($registered.Body.Category -ceq 'Environmental') 'Category must be canonical.'
    Assert ($registered.Location -eq "/api/sensors/$($registered.Body.Id)") 'Registration Location header missing.'
    Assert ((Request GET $registered.Location).Body.DeviceId -eq 'AA:BB:CC:DD:EE:01') 'Registration GET by ID failed.'
    foreach ($mac in @('aa:bb:cc:dd:ee:01', 'AABBCCDDEE01', 'AA-BB-CC-DD-EE-01')) {
        Assert ((Request POST '/api/sensors' @{ DeviceId = $mac; Location = 'Zone B'; Category = 'Environmental' }).Code -eq 409) 'MAC variants must not create duplicate devices.'
    }
    foreach ($category in @('Power Consumption', 'Actuator')) {
        Assert ((Request POST '/api/sensors' @{ DeviceId = $category.Replace(' ', '-'); Location = 'Room 1'; Category = $category }).Code -eq 201) 'Device category was rejected.'
    }
    Assert ((Request POST '/api/sensors' @{ DeviceId = 'actuator'; Location = 'Room 1'; Category = 'Actuator' }).Code -eq 409) 'Unique IDs must reject case-only duplicates.'
    foreach ($invalid in @(
        '{}', '{broken', 'null',
        '{"DeviceId":"AA:GG:CC:DD:EE:01","Location":"Zone B","Category":"Environmental"}',
        '{"DeviceId":"P-1","Location":"   ","Category":"Environmental"}',
        '{"DeviceId":"P-1","Location":"Zone B","Category":"Temperature"}',
        '{"DeviceId":null,"Location":null,"Category":null}',
        '{"DeviceId":"P-1","Location":"Zone\nB","Category":"Actuator"}'
    )) { Assert ((Request POST '/api/sensors' $invalid).Code -eq 400) 'Invalid registration must return 400.' }
    Assert ((Request POST '/api/sensors' @{ DeviceId = ('X' * 65); Location = 'Room 1'; Category = 'Actuator' }).Code -eq 400) 'Overlong identifier accepted.'
    Assert ((Request POST '/api/sensors' @{ DeviceId = 'P-1'; Location = ('X' * 81); Category = 'Actuator' }).Code -eq 400) 'Overlong location accepted.'
    Assert ((Request GET '/api/sensors/missing').Code -eq 404) 'Unknown registration must return 404.'
    Assert ((Request GET '/api/sensors').Body.Count -eq 3) 'Rejected records altered registry.'
    Assert ((Request GET '/api/telemetry').Body.Count -eq 0) 'API must start empty.'
    $saved = Request POST '/api/telemetry' (Reading 'API-1' -15 -2)
    Assert ($saved.Code -eq 201) ('POST must return 201. Response: ' + ($saved | ConvertTo-Json -Depth 8 -Compress))
    Assert ($saved.Location -eq "/api/telemetry/$($saved.Body.Id)") 'Created resource location missing.'
    Assert ((Request GET $saved.Location).Body.Value -eq -15) 'GET by ID must return saved reading.'
    Assert ((Request POST '/api/telemetry' (Reading 'API-1' 86)).Code -eq 201) 'Second reading failed.'
    Assert ((Request POST '/api/telemetry' (Reading 'OFFLINE-1' 40 -10)).Code -eq 201) 'Historical reading failed.'
    Assert ((Request GET '/api/telemetry?sensorId=API-1&limit=1').Body.Value -eq 86) 'Latest/filter query failed.'
    Assert ((Request GET '/api/telemetry?location=Missing').Body.Count -eq 0) 'Empty filter failed.'
    Assert ((Request GET '/api/telemetry?category=Temperature').Body.Count -eq 3) 'Category filter failed.'
    $from = [Uri]::EscapeDataString([DateTimeOffset]::UtcNow.AddMinutes(-3).ToString('O'))
    Assert ((Request GET "/api/telemetry?from=$from").Body.Count -eq 2) 'Time filter failed.'
    Assert ((Request GET '/api/telemetry?from=invalid').Code -eq 400) 'Invalid date must return 400.'
    Assert ((Request GET '/api/telemetry?limit=10001').Code -eq 400) 'Invalid limit must return 400.'
    Assert ((Request GET '/api/telemetry/missing').Code -eq 404) 'Missing ID must return 404.'
    Assert ((Request POST '/api/telemetry' '{}').Code -eq 400) 'Missing fields must return 400.'
    Assert ((Request POST '/api/telemetry' '{broken').Code -eq 400) 'Malformed JSON must return 400.'
    Assert ((Request POST '/api/telemetry' (Reading 'BAD-H' 101 -1 'Humidity')).Code -eq 400) 'Invalid humidity must return 400.'
    Assert ((Request POST '/api/telemetry' (Reading 'FUTURE' 40 20)).Code -eq 400) 'Future reading must return 400.'
    $missingValue = Reading 'MISSING' 0
    $missingValue.Remove('Value')
    Assert ((Request POST '/api/telemetry' $missingValue).Code -eq 400) 'Missing value must not become zero.'
    $conflict = Reading 'API-1' 55
    $conflict.Location = 'Zone B'
    Assert ((Request POST '/api/telemetry' $conflict).Code -eq 409) 'Conflicting sensor metadata must return 409.'
    Assert ((Request GET '/api/telemetry').Body.Count -eq 3) 'Invalid requests must not be stored.'
    $pending = @()
    $contents = @()
    try {
        for ($i = 0; $i -lt 5; $i++) {
            $content = [System.Net.Http.StringContent]::new((ConvertTo-Json (Reading 'API-1' (40 + $i) -4) -Compress), [Text.Encoding]::UTF8, 'application/json')
            $contents += $content
            $pending += $http.PostAsync("$baseUri/api/telemetry", $content)
        }
        foreach ($task in $pending) {
            $response = $task.GetAwaiter().GetResult()
            try { Assert ([int]$response.StatusCode -eq 201) 'Concurrent submission failed.' }
            finally { $response.Dispose() }
        }
    } finally { foreach ($content in $contents) { $content.Dispose() } }
    Assert ((Request GET '/api/telemetry').Body.Count -eq 8) 'Concurrent writes lost data.'
    Stop-Api
    Start-Api
    Assert ((Request GET '/api/telemetry').Body.Count -eq 8) 'Readings must survive restart.'
    Assert ((Request GET '/api/sensors').Body.Count -eq 3) 'Registrations must survive restart independently of readings.'
    Assert ((Request GET $registered.Location).Body.DeviceId -eq 'AA:BB:CC:DD:EE:01') 'Stored registration identity changed after restart.'
    Write-Output 'PASS: registration API, all categories, MAC normalization, duplicates, invalid input, and persistence.'
    Write-Output 'PASS: API creation, retrieval, filtering, validation, conflicts, and persistence.'

    $assembly = [Reflection.Assembly]::LoadFrom($clientDll)
    [System.Windows.Application]::ResourceAssembly = $assembly
    $client = New-Object ST10443277_PROG7312.TelemetryApiClient
    Assert ($client.GetReadingsAsync().GetAwaiter().GetResult().Count -eq 8) 'WPF HttpClient cannot retrieve API JSON.'
    $window = New-Object ST10443277_PROG7312.MainWindow
    # Host controls off-screen so WPF completes its Loaded and viewport sizing lifecycle.
    $window.WindowStartupLocation = 'Manual'
    $window.Left = -10000
    $window.Top = -10000
    $window.ShowInTaskbar = $false
    $window.ShowActivated = $false
    $window.Show()
    Click $window.FindName('OpenTelemetryButton')
    $dashboard = $window.FindName('TelemetryView')
    Click (Control 'RefreshButton')
    Pump-Until { (Control 'RefreshButton').IsEnabled }
    Assert ((Control 'DataMode').Text -eq 'API CONNECTED') ('Dashboard failed to connect: ' + (Control 'ApiStatus').Text)
    Assert ((Control 'OnlineCount').Text -eq '1') 'Dashboard online count incorrect.'
    Assert ((Control 'DisconnectedCount').Text -eq '1') 'Dashboard disconnected count incorrect.'
    Assert ((Control 'AlertCount').Text -eq '1') 'Dashboard alert count incorrect.'
    Assert ((Control 'AlertList').Items[0].Detail.Contains('86')) 'Alert must show actual submitted value.'
    $registrationPanel = Control 'RegistrationPanel'
    function RegControl($name) { $registrationPanel.FindName($name) }
    Click (RegControl 'RefreshRegistryButton')
    Pump-Until { (RegControl 'RefreshRegistryButton').IsEnabled }
    Assert ((RegControl 'RegistryGrid').Items.Count -eq 3) 'WPF registry failed to retrieve records.'
    Click (RegControl 'RegisterButton')
    Assert ((RegControl 'DeviceIdError').Text.Length -gt 0) 'Missing device ID not highlighted.'
    Assert ((RegControl 'LocationError').Text.Length -gt 0) 'Missing deployment location not highlighted.'
    Assert ((RegControl 'CategoryError').Text.Length -gt 0) 'Missing category not highlighted.'
    Click (RegControl 'SimulateButton')
    $simulatedId = (RegControl 'DeviceIdInput').Text
    Assert ($simulatedId -match '^([0-9A-F]{2}:){5}[0-9A-F]{2}$') 'Simulation must produce a MAC address.'
    Assert (([Convert]::ToInt32($simulatedId.Substring(0,2),16) -band 3) -eq 2) 'Simulated MAC must be locally administered and unicast.'
    Assert ((RegControl 'LocationInput').Text.Contains('Zone')) 'Simulation must provide a deployment location.'
    Assert ((RegControl 'CategoryInput').SelectedIndex -ge 0) 'Simulation must select a category.'
    Assert ((Request GET '/api/sensors').Body.Count -eq 3) 'Simulation must not save without Register.'
    Click (RegControl 'RegisterButton')
    Pump-Until { (RegControl 'RegisterButton').IsEnabled }
    Assert ((RegControl 'FormStatus').Text.StartsWith('Registered ')) 'Simulated registration failed.'
    Assert ((RegControl 'RegistryGrid').Items.Count -eq 4) 'Saved simulated sensor missing from table.'
    $registrationPanel.LoadDraftFromJson('{"DeviceId":"custom-node.1","Location":"Utility room / Zone C","Category":"Power Consumption"}')
    Assert ((RegControl 'DeviceIdInput').Text -eq 'custom-node.1') 'JSON import did not fill the form.'
    foreach ($invalidDraft in @('{broken', '[]', '{"DeviceId":123,"Location":"Room","Category":"Actuator"}', '{"DeviceId":"ok","Location":"Room","Category":"Invalid"}', ('X' * 16385))) {
        $failed = $false
        try { $registrationPanel.LoadDraftFromJson($invalidDraft) } catch { $failed = $true }
        Assert $failed 'Invalid import must be rejected.'
        Assert ((RegControl 'DeviceIdInput').Text -eq 'custom-node.1') 'Invalid import must preserve the existing draft.'
    }
    Click (RegControl 'RegisterButton')
    Pump-Until { (RegControl 'RegisterButton').IsEnabled }
    Assert ((Request GET '/api/sensors').Body.Count -eq 5) 'Imported registration was not persisted.'
    Assert ((RegControl 'DeviceIdInput').Text -eq '') 'Successful save must clear the form.'
    $registrationPanel.LoadDraftFromJson('{"DeviceId":"CUSTOM-NODE.1","Location":"Room","Category":"Actuator"}')
    Click (RegControl 'RegisterButton')
    Pump-Until { (RegControl 'RegisterButton').IsEnabled }
    Assert ((RegControl 'FormStatus').Text.Contains('already registered')) 'Duplicate error not displayed.'
    Assert ((RegControl 'DeviceIdInput').Text -eq 'CUSTOM-NODE.1') 'Duplicate rejection must preserve the draft.'
    Click (RegControl 'ClearButton')
    Assert ((RegControl 'CategoryInput').SelectedIndex -eq -1) 'Clear form did not reset category.'
    Write-Output 'PASS: WPF required fields, simulation, import, invalid import preservation, save, duplicates, and clear.'
    (Control 'InputCategory').SelectedIndex = 1
    (Control 'InputSensor').Text = 'BAD-UI'
    (Control 'InputValue').Text = '101'
    Click (Control 'SendReadingButton')
    Pump-Until { (Control 'SendReadingButton').IsEnabled }
    Assert ((Control 'SubmitStatus').Text.Contains('Humidity must be between')) 'API field-validation details not displayed.'
    (Control 'InputCategory').SelectedIndex = 0
    (Control 'InputSensor').Text = 'UI-1'
    (Control 'InputValue').Text = '89.5'
    Click (Control 'SendReadingButton')
    Pump-Until { (Control 'SendReadingButton').IsEnabled }
    Assert ((Control 'SubmitStatus').Text.StartsWith('Saved UI-1')) ('Dashboard submit failed: ' + (Control 'SubmitStatus').Text)
    Assert ((Request GET '/api/telemetry?sensorId=UI-1').Body.Value -eq 89.5) 'Dashboard did not POST to the API.'
    Assert ((Control 'OnlineCount').Text -eq '2') 'Dashboard did not retrieve its submitted reading.'
    $savedStatus = (Control 'SubmitStatus').Text
    (Control 'InputValue').Text = 'not a number'
    Click (Control 'SendReadingButton')
    Assert ((Control 'SubmitStatus').Text -eq 'Enter a valid numeric reading.') 'Invalid UI input was not rejected.'
    (Control 'InputValue').Text = '89.5'
    (Control 'SubmitStatus').Text = $savedStatus
    (Control 'SensorFilter').SelectedItem = 'UI-1'
    Assert ((Control 'ChartCaption').Text.StartsWith('UI-1')) 'Sensor filter failed after loading API data.'
    $root = $window.Content
    $window.Width = 1240
    $window.Height = 1400
    $window.UpdateLayout()
    Pump-Until { $registrationPanel.ActualWidth -gt 1100 }
    Pump-Until { (RegControl 'RegistryGrid').Columns[0].ActualWidth -gt 200 }
    Assert ((Control 'LatestPoint').Visibility -eq 'Visible') 'Single reading must be visible on chart.'
    Click (Control 'ResetFiltersButton')
    $root.UpdateLayout()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(1240,1400,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($root)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $testDirectory 'dashboard-api.png'))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    function Save-RegistrationImage($name) {
        $registrationPanel.UpdateLayout()
        Pump-Until {
            $columnsWidth = ((RegControl 'RegistryGrid').Columns | Measure-Object -Property ActualWidth -Sum).Sum
            $tableWidth = (RegControl 'RegistryGrid').ActualWidth
            $columnsWidth -le $tableWidth -and $columnsWidth -ge $tableWidth - 40
        }
        foreach ($column in (RegControl 'RegistryGrid').Columns) {
            Assert ($column.ActualWidth -ge 140) 'Registration column is too narrow to read.'
        }
        $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($registrationPanel.ActualWidth),[int][Math]::Ceiling($registrationPanel.ActualHeight),96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
        # VisualBrush removes the element's offset within its parent when rendering the panel alone.
        $visual = [System.Windows.Media.DrawingVisual]::new()
        $context = $visual.RenderOpen()
        $context.DrawRectangle([System.Windows.Media.VisualBrush]::new($registrationPanel), $null,
            [System.Windows.Rect]::new(0,0,$registrationPanel.ActualWidth,$registrationPanel.ActualHeight))
        $context.Close()
        $bitmap.Render($visual)
        $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $testDirectory $name))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    }
    Save-RegistrationImage 'registration.png'
    $window.Width = 800
    $window.Height = 600
    $window.UpdateLayout()
    Pump-Until { $registrationPanel.ActualWidth -lt 800 -and $registrationPanel.ActualWidth -gt 600 }
    Assert ((Control 'Plot').ActualWidth -gt 0) 'Chart collapsed at minimum window size.'
    Assert ((RegControl 'RegistryGrid').ActualWidth -gt 500) 'Registry collapsed at minimum window size.'
    Save-RegistrationImage 'registration-minimum.png'

    Stop-Api
    $registrationPanel.LoadDraftFromJson('{"DeviceId":"OFFLINE-DRAFT","Location":"Room 2","Category":"Actuator"}')
    Click (RegControl 'RegisterButton')
    Pump-Until { (RegControl 'RegisterButton').IsEnabled }
    Assert ((RegControl 'FormStatus').Text.Contains('could not be confirmed')) 'Offline registration must report failure.'
    Assert ((RegControl 'DeviceIdInput').Text -eq 'OFFLINE-DRAFT') 'Offline save lost the draft.'
    Assert ((RegControl 'RegistryGrid').Items.Count -eq 5) 'Failed save must not add a fictitious record.'
    Click (RegControl 'RefreshRegistryButton')
    Pump-Until { (RegControl 'RefreshRegistryButton').IsEnabled }
    Assert ((RegControl 'RegistryStatus').Text.Contains('previously retrieved')) 'Offline registry must mark cached records.'
    Click (Control 'RefreshButton')
    Pump-Until { (Control 'RefreshButton').IsEnabled }
    Assert ((Control 'DataMode').Text -eq 'API OFFLINE') 'Offline status missing.'
    Assert ((Control 'ApiStatus').Text.Contains('cached data')) 'Cached data must be labelled stale.'
    Assert ((Control 'OnlineCount').Text -eq '2') 'Cached readings were lost.'
    Start-Api
    Click (RegControl 'RefreshRegistryButton')
    Pump-Until { (RegControl 'RefreshRegistryButton').IsEnabled }
    Assert ((RegControl 'RegistryGrid').Items.Count -eq 5) 'WPF registrations did not survive restart.'
    Click (Control 'RefreshButton')
    Pump-Until { (Control 'RefreshButton').IsEnabled }
    Assert ((Control 'DataMode').Text -eq 'API CONNECTED') 'Dashboard failed to recover.'
    Click (Control 'BackButton')
    Assert ($window.FindName('LandingView').Visibility -eq 'Visible') 'Back navigation failed.'
    Assert ((RegControl 'DeviceIdInput').Text -eq 'OFFLINE-DRAFT') 'Navigation lost the registration draft.'
    $window.Close()
    Write-Output 'PASS: real WPF HTTP client, dashboard POST/GET, counts, alerts, filters, single point, resize, offline cache, recovery and navigation.'
    Write-Output "Artifacts: $testDirectory"
} finally {
    Stop-Api
    $http.Dispose()
    $env:SMARTX_API_URL = $previousApiUrl
}



