# Saves the last 24 hours of failed sign-ins and lockouts from the Security log
# (needs an admin terminal) to lockout-report.txt next to this script.
$out = Join-Path $PSScriptRoot 'lockout-report.txt'
$since = (Get-Date).AddHours(-24)

$events = Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4625, 4776, 4740, 4771; StartTime = $since } -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated

$lines = foreach ($e in $events) {
    $x = [xml]$e.ToXml()
    $d = @{}
    foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    '{0:yyyy-MM-dd HH:mm:ss} | {1} | user={2} | logonType={3} | process={4} | workstation={5} | ip={6} | status={7}/{8} | package={9}' -f `
        $e.TimeCreated, $e.Id, ($d.TargetUserName), $d.LogonType, $d.ProcessName, ($d.WorkstationName + $d.Workstation),
        $d.IpAddress, $d.Status, $d.SubStatus, ($d.AuthenticationPackageName + $d.PackageName)
}

$audit = auditpol /get /subcategory:"Logon","Credential Validation" | Out-String
@("Report made $(Get-Date)", "Events found: $(@($events).Count)", $audit) + $lines | Set-Content -Encoding utf8 $out
"Saved $(@($events).Count) events to $out"
