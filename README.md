# Meeting Buddy

Meeting Buddy is a Windows desktop app for managing meetings, recurring schedules, Zoom links and reminders, with optional Elgato Stream Deck integration.

## Features

- Meeting list, monthly calendar, search and meeting details
- Daily summary and missed meeting centre
- Recurring meetings, reminders and configurable working hours
- Import `.ics` calendars and `.zip` calendar exports
- Zoom join links and optional Stream Deck XL actions
- Local storage and rolling daily backups
- System tray integration and optional startup with Windows

## Requirements

- Windows 11
- .NET 10 SDK to build from source (or use a published self-contained release)
- Optional: Elgato Stream Deck and Stream Deck software for the plugin

## Build from source

```powershell
dotnet build .\MeetingBuddy.csproj
dotnet run --project .\MeetingBuddy.csproj
```

To produce a self-contained Windows x64 ZIP, run:

```powershell
.\BuildRelease.ps1
```

Output: `Release/MeetingBuddy-v3.3-win-x64.zip`.

## Stream Deck plugin

The plugin is in `StreamDeckPlugin/com.meetingbuddy.streamdeck.sdPlugin/`. It communicates with the running application using a loopback HTTP API at `127.0.0.1:48291` by default. The plugin version in the uploaded archive's manifest is `3.3.1` (the handover referenced `3.3.2`); validate which package is installed before release.

**Security note:** The local HTTP API has no authentication and sends permissive CORS headers in this version. A security hardening update is recommended before promoting the app for widespread deployment.

## Privacy

Meeting Buddy stores meeting information locally. Imported calendars and local backups may contain meeting titles, meeting URLs, and notes. Do not commit real calendar exports, saved meetings, logs, or work-sensitive information to a public repository.

## Status

The uploaded v3.3 sources were previously reported working on Windows. The repository preparation excludes compiled files and local logs, but the source has **not** been rebuilt or hardware-tested in this environment.

## Licence

No open-source licence has been applied yet. The repository is publicly readable; re-use and redistribution require permission until a licence is selected.
