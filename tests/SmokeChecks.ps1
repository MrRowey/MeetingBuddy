# Source-level regression checks; Windows CI also compiles XAML and code-behind.
$ErrorActionPreference = 'Stop'
$xaml = Get-Content "$PSScriptRoot/../MainWindow.xaml" -Raw
$code = Get-Content "$PSScriptRoot/../MainWindow.xaml.cs" -Raw
$alert = Get-Content "$PSScriptRoot/../AlertWindow.xaml.cs" -Raw
[xml]$parsed = $xaml
foreach ($name in @('WorkspaceHeading','ProfileNameBox','ProfileWorkplaceBox','ProfileJobTitleBox','PlatformTeamsBox','PlatformZoomBox','PlatformMeetBox','CalendarModeToggle','MonthCells','SelectedEventColourPreview')) {
    if ($xaml -notmatch ('x:Name="' + $name + '"')) { throw "Missing XAML control: $name" }
}
foreach ($method in @('ShowFirstRunSetup','CalendarModeToggle_Click','CalendarPrevious_Click','CalendarNext_Click','CalendarToday_Click','EventColor_SelectionChanged','LoadReminderState','SaveReminderState')) {
    if ($code -notmatch [regex]::Escape($method)) { throw "Missing implementation: $method" }
}
if ($code -notmatch 'weeklyCalendarMode \? 7 : 42') { throw 'Weekly grid regression' }
if ($code -notmatch 'LoadReminderState\(\)') { throw 'Reminder state recovery missing' }
if ($alert -notmatch 'SnoozeDurationBox') { throw 'Snooze duration selection missing' }
Write-Host 'Meeting Buddy static regression checks passed.'
