<#
  Registers the "Monthly Cleanup" scheduled task for the current user.
  Runs monthly-cleanup.ps1 on the 1st of every month at 13:00; if the laptop was off or asleep
  then, it runs as soon as possible afterwards. Started through `conhost --headless`, so no
  console window flashes on screen.

  powershell.exe -File install-monthly-cleanup.ps1 [-ScriptPath <path to monthly-cleanup.ps1>]
#>
param([string]$ScriptPath = (Join-Path $PSScriptRoot 'monthly-cleanup.ps1'))

$ScriptPath = (Resolve-Path $ScriptPath).Path
$name = 'Monthly Cleanup'
$taskArgs = "--headless powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$ScriptPath`""
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$esc  = [System.Security.SecurityElement]

# Monthly triggers aren't available through New-ScheduledTaskTrigger, so the task is defined as XML.
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Clears temporary data (Temp, crash dumps, Spotify and browser caches, Recycle Bin items older than 30 days). Log: cleanup.log next to the script.</Description>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>2026-12-01T13:00:00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByMonth>
        <DaysOfMonth><Day>1</Day></DaysOfMonth>
        <Months><January/><February/><March/><April/><May/><June/><July/><August/><September/><October/><November/><December/></Months>
      </ScheduleByMonth>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$($esc::Escape($user))</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>conhost.exe</Command>
      <Arguments>$($esc::Escape($taskArgs))</Arguments>
    </Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName $name -Xml $xml -Force | Out-Null
Get-ScheduledTask -TaskName $name | Get-ScheduledTaskInfo | Select-Object TaskName, NextRunTime
