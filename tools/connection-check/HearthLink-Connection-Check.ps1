$ErrorActionPreference='Continue'
$report=New-Object System.Collections.Generic.List[string]
function Line([string]$value) {$report.Add($value); Write-Host $value}
Line ('HearthLink connection check - '+[Environment]::MachineName)
Line ('Checked at '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Line 'This check reads settings only. No pairing codes, credentials or screen contents are collected.'
Line ''
Line 'ACTIVE CONNECTIONS WITH AN IPv4 GATEWAY'
$interfaces=[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object {$_.OperationalStatus -eq 'Up'}
foreach($adapter in $interfaces) {
    $properties=$adapter.GetIPProperties()
    $gateways=@($properties.GatewayAddresses | Where-Object {$_.Address.AddressFamily -eq 'InterNetwork' -and $_.Address.IPAddressToString -ne '0.0.0.0'})
    if($gateways.Count -gt 0) {
        $addresses=@($properties.UnicastAddresses | Where-Object {$_.Address.AddressFamily -eq 'InterNetwork'} | ForEach-Object {$_.Address.IPAddressToString})
        Line ($adapter.Name+': '+($addresses -join ', ')+'; gateway '+(($gateways | ForEach-Object {$_.Address.IPAddressToString}) -join ', '))
    }
}
Line ''
Line 'WINDOWS NETWORK PROFILES'
$profiles=@(Get-NetConnectionProfile -ErrorAction SilentlyContinue)
foreach($connectionProfile in $profiles) {Line ($connectionProfile.InterfaceAlias+': '+$connectionProfile.NetworkCategory.ToString())}
Line ''
Line 'LOCAL HEARTHLINK LISTENER'
$listeners=@(Get-NetTCPConnection -LocalPort 45831 -State Listen -ErrorAction SilentlyContinue)
if($listeners.Count -eq 0) {Line 'No TCP 45831 listener. Open HearthLink on THIS computer and enable Allow incoming sessions.'}
foreach($listener in $listeners) {
    $process=Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    Line ($listener.LocalAddress+':45831; process '+$process.ProcessName+'; program '+$process.Path)
}
Line ''
Line 'HEARTHLINK FIREWALL RULES'
$rules=@(Get-NetFirewallRule -DisplayName 'HearthLink Host TCP' -ErrorAction SilentlyContinue)
if($rules.Count -eq 0) {Line 'No HearthLink firewall rule found. Repair/re-run the installer and select the incoming-host firewall task.'}
foreach($rule in $rules) {
    $app=$rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
    $ports=$rule | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
    $addresses=$rule | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue
    Line ('Enabled='+$rule.Enabled+'; profile='+$rule.Profile+'; direction='+$rule.Direction+'; action='+$rule.Action)
    Line ('Program='+$app.Program+'; TCP port='+$ports.LocalPort+'; approved sources='+($addresses.RemoteAddress -join ', '))
}
Line ''
Line 'NEXT STEPS'
if($profiles | Where-Object {$_.NetworkCategory -eq 'Public'}) {
    Line 'A connection is Public. The default HearthLink rule permits Private networks only.'
    Line 'If this is your trusted home Ethernet/Wi-Fi connection, change THAT connection to Private in Windows network settings, then retry pairing.'
    Line 'Do not change an untrusted/public connection or disable the firewall.'
}
if($listeners.Count -gt 0 -and $rules.Count -gt 0) {
    Line 'Use this host computer''s gateway-connected IPv4 address on the controller. Open the pairing code on the host.'
    Line 'If the port still times out, check guest Wi-Fi/client isolation and whether the rule''s program matches the running listener.'
}
Line 'Only proceed to fingerprint/code pairing after TCP 45831 is reachable.'
$destination=Join-Path $PSScriptRoot ([Environment]::MachineName+'-HearthLink-connection-report.txt')
try {$report | Set-Content -LiteralPath $destination -Encoding UTF8; Write-Host ''; Write-Host ('Report saved to '+$destination)} catch {Write-Host 'Could not save next to the script. Copy the visible report text instead.'}
