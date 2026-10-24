<#
.SYNOPSIS
    Registers or removes the scheduled task that starts the PC Diagnostic recorder at sign-in (run by the installer).

.DESCRIPTION
    The recorder needs administrator rights for CPU temperature, power and storage health. Instead of a UAC prompt
    at every sign-in, the installer (already elevated, and only when the user keeps the option ticked) registers
    "\PC Diagnostic\Recorder": at the user's sign-in it starts PcDiag.Agent.exe in that user's session with the
    highest rights the user has. It runs at normal priority, on battery too, with no time limit, one instance at a
    time. Removing the program removes the task.

.EXAMPLE
    RecorderTask.ps1 -Register -AgentPath 'C:\Program Files\PC Diagnostic\agent\PcDiag.Agent.exe' -User 'S-1-5-21-...'
    RecorderTask.ps1 -Unregister
    RecorderTask.ps1 -Describe -AgentPath 'C:\x\PcDiag.Agent.exe' -User $env:USERNAME   # builds it without registering
#>
[CmdletBinding(DefaultParameterSetName = 'Describe')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Register')][switch] $Register,
    [Parameter(Mandatory = $true, ParameterSetName = 'Unregister')][switch] $Unregister,
    [Parameter(Mandatory = $true, ParameterSetName = 'Describe')][switch] $Describe,
    [Parameter(Mandatory = $true, ParameterSetName = 'Register')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Describe')][string] $AgentPath,
    # The user the recorder runs for: a SID (preferred) or DOMAIN\name.
    [Parameter(Mandatory = $true, ParameterSetName = 'Register')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Describe')][string] $User
)

$ErrorActionPreference = 'Stop'
$taskPath = '\PC Diagnostic\'
$taskName = 'Recorder'

if ($Unregister) {
    Stop-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    try {
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $service.GetFolder('\').DeleteFolder('PC Diagnostic', 0)
    } catch {
        # Not empty or already gone.
    }
    exit 0
}

if (-not [System.IO.Path]::IsPathRooted($AgentPath) -or -not $AgentPath.EndsWith('PcDiag.Agent.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw "AgentPath must be the full path of PcDiag.Agent.exe (was '$AgentPath')."
}

if ($User -match '^S-1-[0-9-]+$') {
    $sid = New-Object System.Security.Principal.SecurityIdentifier($User)
} else {
    $sid = (New-Object System.Security.Principal.NTAccount($User)).Translate([System.Security.Principal.SecurityIdentifier])
}
$account = $sid.Translate([System.Security.Principal.NTAccount]).Value

$action = New-ScheduledTaskAction -Execute $AgentPath -WorkingDirectory (Split-Path -Parent $AgentPath)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
$trigger.Delay = 'PT10S' # let the desktop settle first
$principal = New-ScheduledTaskPrincipal -UserId $sid.Value -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew -Priority 5 -Compatibility Win8
$task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Starts the PC Diagnostic recorder with administrator rights when you sign in, so it can read CPU temperature, power and storage health. Installed and removed by PC Diagnostic.'

if ($Describe) {
    [pscustomobject]@{
        Task = "$taskPath$taskName"
        Account = $account
        Sid = $sid.Value
        Execute = $task.Actions[0].Execute
        WorkingDirectory = $task.Actions[0].WorkingDirectory
        Trigger = "At logon of $($task.Triggers[0].UserId), delay $($task.Triggers[0].Delay)"
        RunLevel = $task.Principal.RunLevel
        LogonType = $task.Principal.LogonType
        OnBattery = -not $task.Settings.DisallowStartIfOnBatteries
        StopOnBattery = $task.Settings.StopIfGoingOnBatteries
        TimeLimit = $task.Settings.ExecutionTimeLimit
        Instances = $task.Settings.MultipleInstances
        Priority = $task.Settings.Priority
    }
    exit 0
}

Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -InputObject $task -Force | Out-Null
Write-Host "Registered $taskPath$taskName for $account."
