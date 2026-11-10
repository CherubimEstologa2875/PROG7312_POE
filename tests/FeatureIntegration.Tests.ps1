param([int]$Port = 5089)
# Run the original regression suite first, reusing its isolated HTTP/WPF helpers and storage.
. (Join-Path $PSScriptRoot 'ApiIntegration.Tests.ps1') -Port $Port
& (Join-Path $PSScriptRoot 'Part3Algorithms.Tests.ps1')
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(10)
$env:SMARTX_API_URL = $baseUri

function Packet($sensor, $value, $minutes = -1) {
    return @{ SensorId = $sensor.DeviceId; Location = $sensor.Location; Timestamp = [DateTimeOffset]::UtcNow.AddMinutes($minutes).ToString('O'); Value = $value }
}
function Upload($sensorId, $kind, $filename, [byte[]]$bytes, $extra = $false) {
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $form.Add([System.Net.Http.ByteArrayContent]::new($bytes), 'file', $filename)
    if ($extra) { $form.Add([System.Net.Http.ByteArrayContent]::new($bytes), 'file', 'second.txt') }
    try {
        $response = $http.PostAsync("$baseUri/api/sensors/$sensorId/attachments?kind=$kind", $form).GetAwaiter().GetResult()
        try {
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return @{ Code = [int]$response.StatusCode; Body = (ConvertFrom-Json $json); Location = $response.Headers.Location }
        } finally { $response.Dispose() }
    } finally { $form.Dispose() }
}
function ProfileControl($name) { $profile.FindName($name) }
try {
    Start-Api
    $temperature = (Request POST '/api/sensors' @{ DeviceId = 'TYPED-TEMP'; Location = 'Facility A / Zone 1 / Sub-Zone B'; Category = 'Environmental' }).Body
    $meter1 = (Request POST '/api/sensors' @{ DeviceId = 'METER-1'; Location = 'Utility room'; Category = 'Power Consumption' }).Body
    $meter2 = (Request POST '/api/sensors' @{ DeviceId = 'METER-2'; Location = 'Room 2'; Category = 'Power Consumption' }).Body
    $valve = (Request POST '/api/sensors' @{ DeviceId = 'VALVE-1'; Location = 'Pump room'; Category = 'Actuator' }).Body
    Assert ((Request POST '/api/packets/temperature' (Packet $temperature 22 -2)).Code -eq 201) 'Float packet failed.'
    $latestTemperature = Request POST '/api/packets/temperature' (Packet $temperature 88)
    Assert ($latestTemperature.Code -eq 201) 'Second float packet failed.'
    Assert ((Request GET $latestTemperature.Location).Body.Value -eq 88) 'Typed resource Location failed.'
    Assert ((Request POST '/api/packets/power' (Packet $meter1 1000 -2)).Code -eq 201) 'First integer packet failed.'
    Assert ((Request POST '/api/packets/power' (Packet $meter1 2200)).Code -eq 201) 'Second integer packet failed.'
    Assert ((Request POST '/api/packets/power' (Packet $meter2 500)).Code -eq 201) 'Second meter failed.'
    Assert ((Request POST '/api/packets/actuator' (Packet $valve $false -2)).Code -eq 201) 'False boolean packet failed.'
    Assert ((Request POST '/api/packets/actuator' (Packet $valve $true)).Code -eq 201) 'True boolean packet failed.'
    $typed = (Request GET '/api/packets').Body
    Assert ($typed.Actuator[0].Value -is [bool] -and !$typed.Actuator[0].Value) 'Boolean payload was coerced or false was lost.'
    Assert ((Request POST '/api/packets/power' (Packet $meter1 12.5)).Code -eq 400) 'Fractional integer must be rejected.'
    Assert ((Request POST '/api/packets/power' (Packet $meter1 '12')).Code -eq 400) 'String payload must not become an integer.'
    Assert ((Request POST '/api/packets/power' (Packet $meter1 2147483648)).Code -eq 400) 'Overflowing integer must be rejected.'
    Assert ((Request POST '/api/packets/actuator' (Packet $valve 1)).Code -eq 400) 'Numeric boolean must be rejected.'
    Assert ((Request POST '/api/packets/temperature' (Packet $temperature -300)).Code -eq 400) 'Invalid float range accepted.'
    Assert ((Request POST '/api/packets/temperature' (Packet $meter1 25)).Code -eq 400) 'Profile category mismatch accepted.'
    $missing = Packet $valve $true
    $missing.Remove('Value')
    Assert ((Request POST '/api/packets/actuator' $missing).Code -eq 400) 'Missing boolean must not become false.'
    $wrong = Packet $temperature 24
    $wrong.Location = 'Somewhere else'
    Assert ((Request POST '/api/packets/temperature' $wrong).Code -eq 400) 'Wrong deployment location accepted.'

    # Exercise the actual generic batching class across a full batch and a short tail.
    $packets = [System.Collections.Generic.List[SmartX.Contracts.TelemetryPacket[int]]]::new()
    for ($i = 0; $i -lt 257; $i++) {
        $p = [SmartX.Contracts.TelemetryPacket[int]]::new()
        $p.Value = $i
        $packets.Add($p)
    }
    $batches = [SmartX.Contracts.TelemetryBatches[int]]::Stage($packets, 256)
    Assert ($batches.Length -eq 2 -and $batches[0].Length -eq 256 -and $batches[1].Length -eq 1) 'Jagged batch boundaries incorrect.'
    $flattened = [SmartX.Contracts.TelemetryBatches[int]]::ToHistory($batches)
    Assert ($flattened.Count -eq 257 -and $flattened[256].Value -eq 256) 'Batch-to-list transfer changed order.'
    $a = [SmartX.Contracts.PowerReading]::new(2200)
    $b = [SmartX.Contracts.PowerReading]::new(500)
    Assert (([SmartX.Contracts.PowerReading]::op_Addition($a,$b)).Watts -eq 2700) 'Power addition failed.'
    Assert (([SmartX.Contracts.PowerReading]::op_Subtraction($b,$a)).Watts -eq -1700) 'Signed power delta failed.'
    Assert ([SmartX.Contracts.PowerReading]::op_GreaterThan($a,$b)) 'Power comparison failed.'

    $tree = @{ Name = 'Facility A'; Enabled = $true; Children = @(@{
        Name = 'Zone 1'; Enabled = $true; Children = @(@{ Name = 'Sub-Zone B'; Enabled = $true; SensorId = $temperature.DeviceId; Children = @() })
    }) }
    $treeJson = ConvertTo-Json $tree -Depth 20
    $validation = Request POST "/api/sensors/$($temperature.Id)/deployment/validate" $treeJson
    Assert ($validation.Body.IsValid -and $validation.Body.Paths[0].Contains('Sub-Zone B')) 'Recursive valid tree rejected.'
    $tree.Enabled = $false
    $invalidTree = ConvertTo-Json $tree -Depth 20
    Assert (!(Request POST "/api/sensors/$($temperature.Id)/deployment/validate" $invalidTree).Body.IsValid) 'Disabled ancestor was not detected.'
    $tree.Enabled = $true
    $tree.Children[0].Children[0].SensorId = 'UNREGISTERED'
    Assert (!(Request POST "/api/sensors/$($temperature.Id)/deployment/validate" (ConvertTo-Json $tree -Depth 20)).Body.IsValid) 'Unknown sensor was not detected.'
    $cycle = [SmartX.Contracts.DeploymentNode]::new()
    $cycle.Name = 'Cycle'
    $cycle.Children.Add($cycle)
    $list = [System.Collections.Generic.List[SmartX.Contracts.SensorRegistration]]::new()
    Assert (![SmartX.Contracts.DeploymentValidator]::Validate($cycle,$list,'missing').IsValid) 'Cycle protection failed.'

    $logBytes = [Text.Encoding]::UTF8.GetBytes("boot successful`nreading received`n")
    $log = Upload $temperature.Id 'Log' 'device.log' $logBytes
    Assert ($log.Code -eq 201) ('Log upload failed: ' + ($log.Body | ConvertTo-Json -Compress))
    Assert ($log.Body.Size -eq $logBytes.Length) 'Stored file size is incorrect.'
    Assert ($log.Body.Sha256.Length -eq 64) 'Attachment digest missing.'
    Assert ((Request GET "/api/sensors/$($meter1.Id)/attachments").Body.Count -eq 0) 'File leaked into another profile.'
    Assert ((Request GET "/api/sensors/$($meter1.Id)/attachments/$($log.Body.Id)").Code -eq 404) 'Cross-profile download must be rejected.'
    Assert ((Upload $temperature.Id 'Log' 'empty.log' ([byte[]]@())).Code -eq 400) 'Empty file accepted.'
    Assert ((Upload $temperature.Id 'Log' 'run.exe' $logBytes).Code -eq 400) 'Wrong file extension accepted.'
    Assert ((Upload $temperature.Id 'Photo' 'fake.png' $logBytes).Code -eq 400) 'Invalid image signature accepted.'
    Assert ((Upload $temperature.Id 'Log' 'first.log' $logBytes $true).Code -eq 400) 'Multiple files accepted by single-file endpoint.'
    Assert ((Upload 'missing' 'Log' 'device.log' $logBytes).Code -eq 404) 'Upload to unknown profile accepted.'
    Assert ((Upload $temperature.Id 'Deployment' 'deployment.json' ([Text.Encoding]::UTF8.GetBytes($invalidTree))).Code -eq 400) 'Unsafe deployment profile attached.'
    Assert ((Upload $temperature.Id 'Deployment' 'deployment.json' ([Text.Encoding]::UTF8.GetBytes($treeJson))).Code -eq 201) 'Valid deployment profile upload failed.'
    Assert ((Upload $temperature.Id 'Configuration' 'device.json' ([Text.Encoding]::UTF8.GetBytes('{"interval":15}'))).Code -eq 201) 'Configuration upload failed.'
    $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aC1sAAAAASUVORK5CYII=')
    Assert ((Upload $temperature.Id 'Photo' 'deployment.png' $png).Code -eq 201) 'Photo upload failed.'
    $tooLarge = New-Object byte[] (10 * 1024 * 1024 + 1)
    Assert ((Upload $temperature.Id 'Log' 'large.log' $tooLarge).Code -eq 413) 'Oversized upload must return 413.'
    Assert ((Request GET "/api/sensors/$($temperature.Id)/attachments").Body.Count -eq 4) 'Rejected uploads changed the file list.'
    Assert (@(Get-ChildItem $attachmentRoot -Filter '*.upload').Count -eq 0) 'Incomplete upload left temporary files.'
    $download = Join-Path $testDirectory 'downloaded.log'
    $null = $client.DownloadAttachmentAsync($temperature.Id,$log.Body.Id,$download,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($download)) -eq [Convert]::ToBase64String($logBytes)) 'Downloaded bytes differ from uploaded bytes.'
    Stop-Api
    Start-Api
    Assert ((Request GET '/api/packets').Body.Power.Count -eq 3) 'Typed packets did not survive restart.'
    Assert ((Request GET "/api/sensors/$($temperature.Id)/attachments").Body.Count -eq 4) 'Attachments did not survive restart.'
    Write-Output 'PASS: typed packets, exact bool/int handling, generic batching, overloaded operators, recursive deployment checks, streamed uploads, file isolation, limits, downloads and persistence.'

    $window = New-Object ST10443277_PROG7312.MainWindow
    $window.WindowStartupLocation = 'Manual'; $window.Left = -10000; $window.Top = -10000
    $window.ShowInTaskbar = $false; $window.ShowActivated = $false; $window.Show()
    Click $window.FindName('OpenTelemetryButton')
    $dashboard = $window.FindName('TelemetryView')
    Click (Control 'RefreshButton')
    Pump-Until { (Control 'RefreshButton').IsEnabled }
    $registrationPanel = Control 'RegistrationPanel'
    Pump-Until { (RegControl 'RefreshRegistryButton').IsEnabled }
    $profile = $registrationPanel.FindName('ProfilePanel')
    (RegControl 'RegistryGrid').SelectedItem = @((RegControl 'RegistryGrid').Items | Where-Object DeviceId -eq 'METER-2')[0]
    Pump-Until { (ProfileControl 'FileStatus').Text.Contains('No files') }
    (ProfileControl 'PacketValue').Text = '700'
    Click (ProfileControl 'SendPacketButton')
    Pump-Until { (ProfileControl 'SendPacketButton').IsEnabled -and (Control 'RefreshButton').IsEnabled }
    Assert ((ProfileControl 'ProfileStatus').Text.Contains('reading saved')) 'WPF typed packet send failed.'
    Assert ((Control 'PowerSummary').Text.Contains('2900 W')) 'Live power aggregation must use each meter latest value.'
    (Control 'SensorFilter').SelectedItem = 'METER-1'
    (Control 'CategoryFilter').SelectedItem = 'Power'
    Assert ((Control 'InvestigationSummary').Text.Contains('+1200 W')) 'Operator-based power change not displayed.'
    Assert ((Control 'AlertList').Items[0].Detail.Contains('connected load')) 'Contextual troubleshooting is missing.'
    Click (Control 'ResetFiltersButton')
    $alert = @((Control 'AlertList').Items | Where-Object SensorId -eq 'TYPED-TEMP')[0]
    $button = [System.Windows.Controls.Button]::new(); $button.DataContext = $alert
    $method = $dashboard.GetType().GetMethod('Investigate_Click',[Reflection.BindingFlags]'Instance,NonPublic')
    $null = $method.Invoke($dashboard,@($button,[System.Windows.RoutedEventArgs]::new()))
    Assert ((Control 'SensorFilter').SelectedItem -eq 'TYPED-TEMP') 'Alert investigation did not select its sensor.'
    Assert ((Control 'CategoryFilter').SelectedItem -eq 'Temperature') 'Alert investigation chose the wrong stream.'
    Assert ((Control 'InvestigationSummary').Text.Contains('+66')) 'Temperature spike context missing.'
    (Control 'SensorFilter').SelectedItem = 'VALVE-1'
    (Control 'CategoryFilter').SelectedItem = 'Actuator'
    Assert ((Control 'InvestigationSummary').Text.Contains('On')) 'Boolean state must display as On/Off.'
    Assert ((Control 'AlertCount').Text -eq '0') 'On state must not be treated as a numeric threshold anomaly.'
    (RegControl 'RegistryGrid').SelectedItem = @((RegControl 'RegistryGrid').Items | Where-Object DeviceId -eq 'TYPED-TEMP')[0]
    Pump-Until { (ProfileControl 'AttachmentList').Items.Count -eq 4 }
    $uploadTask = $profile.UploadFileAsync($download,'Log')
    Pump-Until { $uploadTask.IsCompleted }
    $null = $uploadTask.GetAwaiter().GetResult()
    Assert ((ProfileControl 'AttachmentList').Items.Count -eq 5) 'WPF streaming upload did not refresh the selected profile.'
    Assert ((ProfileControl 'ProfileStatus').Text.Contains('TYPED-TEMP')) 'Upload confirmation must identify its sensor.'
    $window.Width = 1000; $window.Height = 900; $window.UpdateLayout()
    Pump-Until { $profile.ActualWidth -gt 500 }
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $dc.DrawRectangle([System.Windows.Media.VisualBrush]::new($profile),$null,[System.Windows.Rect]::new(0,0,$profile.ActualWidth,$profile.ActualHeight)); $dc.Close()
    $image = [System.Windows.Media.Imaging.RenderTargetBitmap]::new([int]$profile.ActualWidth,[int]$profile.ActualHeight,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $image.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($image))
    $stream = [IO.File]::Create((Join-Path $testDirectory 'sensor-profile.png'))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    (Control 'SensorFilter').SelectedItem = 'TYPED-TEMP'
    (Control 'CategoryFilter').SelectedItem = 'Temperature'
    $window.UpdateLayout()
    foreach ($entry in @(@{ Name = 'ChartSection'; File = 'investigation-chart.png' }, @{ Name = 'AlertList'; File = 'investigation-alerts.png' })) {
        $element = Control $entry.Name
        $visual = [System.Windows.Media.DrawingVisual]::new()
        $dc = $visual.RenderOpen()
        $rect = [System.Windows.Rect]::new(0,0,$element.ActualWidth,$element.ActualHeight)
        $dc.DrawRectangle([System.Windows.Media.Brushes]::Black,$null,$rect)
        $dc.DrawRectangle([System.Windows.Media.VisualBrush]::new($element),$null,$rect)
        $dc.Close()
        $image = [System.Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($element.ActualWidth),[int][Math]::Ceiling($element.ActualHeight),96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
        $image.Render($visual)
        $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($image))
        $stream = [IO.File]::Create((Join-Path $testDirectory $entry.File))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    }
    $window.Close()
    Write-Output 'PASS: WPF typed submission, power total and delta, contextual alerts, investigation navigation, boolean display and profile attachment flow.'
    Write-Output "Feature artifacts: $testDirectory"
} finally {
    if ($window) { $window.Close() }
    Stop-Api
    $http.Dispose()
    $env:SMARTX_API_URL = $previousApiUrl
}
