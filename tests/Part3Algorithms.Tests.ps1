$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$clientDll = Join-Path $repo 'ST10443277_PROG7312/ST10443277_PROG7312/bin/Debug/ST10443277_PROG7312.exe'
if (!(Test-Path $clientDll)) { throw "Build the Debug solution before running Part 3 tests: $clientDll" }
[void][Reflection.Assembly]::LoadFrom($clientDll)

function Assert($condition, $message) {
    if (!$condition) { throw $message }
}

# Exercise AVL rotations with ordered input, inclusive range bounds, and
# multiple readings sharing one timestamp.
$tree = [SmartX.Contracts.TelemetryAvlTree]::new()
$origin = [DateTimeOffset]::Parse('2026-01-01T00:00:00Z')
for ($i = 0; $i -lt 4096; $i++) {
    $reading = [SmartX.Contracts.TelemetryReading]::new()
    $reading.SensorId = 'AVL-UNIT'
    $reading.Timestamp = $origin.AddMinutes($i).ToString('O')
    $reading.Value = $i
    $tree.Add($origin.AddMinutes($i), $reading)
}
$duplicate = [SmartX.Contracts.TelemetryReading]::new()
$duplicate.SensorId = 'AVL-UNIT'
$duplicate.Timestamp = $origin.AddMinutes(2048).ToString('O')
$duplicate.Value = 9999
$tree.Add($origin.AddMinutes(2048), $duplicate)
$range = $tree.Range($origin.AddMinutes(2047), $origin.AddMinutes(2049))
Assert ($tree.Count -eq 4096) 'AVL Count must count distinct timestamp keys.'
Assert ($tree.IsBalanced()) 'AVL tree lost its balance after monotonic inserts.'
Assert ($tree.Height -le 16) 'AVL height exceeded the expected logarithmic bound.'
Assert ($range.Count -eq 4) 'AVL range should include bounds and all readings at a matching timestamp.'
Assert ($tree.Range($origin.AddMinutes(10), $origin).Count -eq 0) 'Reversed AVL range should be empty.'
$offsetTree = [SmartX.Contracts.TelemetryAvlTree]::new()
foreach ($entry in @(
    @{ Key = [DateTimeOffset]::Parse('2026-01-01T10:00:00+02:00'); Value = 1 },
    @{ Key = [DateTimeOffset]::Parse('2026-01-01T09:00:00+00:00'); Value = 2 }
)) {
    $reading = [SmartX.Contracts.TelemetryReading]::new()
    $reading.Value = $entry.Value
    $offsetTree.Add($entry.Key, $reading)
}
$offsetRange = $offsetTree.Range([DateTimeOffset]::Parse('2026-01-01T07:00:00Z'), [DateTimeOffset]::Parse('2026-01-01T10:00:00Z'))
Assert ($offsetRange[0].Value -eq 1 -and $offsetRange[1].Value -eq 2) 'AVL range must order timestamps by their UTC instants, not ISO string characters.'

# Verify traversal excludes failed nodes, recovery neighbours are reported,
# and the Kruskal tree and route contain the expected endpoints.
$nodes = [System.Collections.Generic.List[SmartX.Contracts.MeshNode]]::new()
foreach ($entry in @(
    @{ Id = 'gateway'; X = 50.0; Y = 50.0; Gateway = $true; Online = $true },
    @{ Id = 'node-a'; X = 20.0; Y = 50.0; Gateway = $false; Online = $true },
    @{ Id = 'node-b'; X = 50.0; Y = 20.0; Gateway = $false; Online = $false },
    @{ Id = 'node-c'; X = 80.0; Y = 50.0; Gateway = $false; Online = $true },
    @{ Id = 'node-d'; X = 50.0; Y = 80.0; Gateway = $false; Online = $true }
)) {
    $node = [SmartX.Contracts.MeshNode]::new()
    $node.Id = $entry.Id; $node.Label = $entry.Id; $node.X = $entry.X; $node.Y = $entry.Y
    $node.Gateway = $entry.Gateway; $node.Online = $entry.Online
    $nodes.Add($node)
}
$links = [SmartX.Contracts.MeshRouting]::BuildCompleteGraph($nodes)
$reachable = [SmartX.Contracts.MeshGraphTraversal]::BreadthFirst($nodes, $links, 'gateway', [Func[SmartX.Contracts.MeshNode,bool]]{ param($n) $n.Gateway -or $n.Online })
Assert ($reachable -notcontains 'node-b') 'BFS traversed an offline node.'
Assert ($reachable -contains 'node-c') 'BFS failed to reach an online node through the active mesh.'
Assert (([SmartX.Contracts.MeshGraphTraversal]::BreadthFirst($nodes, $links, 'unknown')).Count -eq 0) 'BFS should return no nodes for an unknown start.'
$recovery = [SmartX.Contracts.MeshGraphTraversal]::FindRecoveryHops($nodes, $links)
Assert ($recovery.ContainsKey('node-b') -and $recovery['node-b'].Count -gt 0) 'Offline node recovery neighbours are missing.'
$mst = [SmartX.Contracts.MeshRouting]::MinimumSpanningTree($nodes)
Assert ($mst.Count -eq $nodes.Count - 1) 'Kruskal MST must contain n-1 edges for a connected graph.'
$route = [SmartX.Contracts.MeshRouting]::ShortestRoute($nodes, $mst, 'gateway', 'node-c')
Assert ($route.Count -gt 1 -and $route[0].NodeId -eq 'gateway' -and $route[-1].NodeId -eq 'node-c') 'Shortest route did not connect gateway to the selected sensor.'

# Keep graph generation sparse as the simulated network grows.
$largeNodes = [System.Collections.Generic.List[SmartX.Contracts.MeshNode]]::new()
for ($i = 0; $i -lt 2000; $i++) {
    $node = [SmartX.Contracts.MeshNode]::new()
    $node.Id = 'scale-' + $i
    $node.Label = $node.Id
    $node.X = $i % 100
    $node.Y = [Math]::Floor($i / 100)
    $largeNodes.Add($node)
}
$largeLinks = [SmartX.Contracts.MeshRouting]::BuildCompleteGraph($largeNodes)
Assert ($largeLinks.Count -le 2 * $largeNodes.Count) 'Mesh edge generation is no longer sparse.'
Assert (([SmartX.Contracts.MeshRouting]::MinimumSpanningTree($largeNodes)).Count -eq $largeNodes.Count - 1) 'Large simulated mesh is not connected by its MST.'

Write-Output 'PASS: AVL balance and range boundaries, mesh BFS availability, recovery hops, Kruskal MST, route endpoints and sparse 2,000-node graph.'
