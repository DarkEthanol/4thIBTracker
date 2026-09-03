# 4thIB Tracker

A configurable desktop companion app for platoon administration. It replaces the pile of Google Sheets
browser tabs with one window: native views for the things you check constantly
(dashboard, attendance, courses, CEFO) plus embedded browser tabs for everything else.

Built with C# / WPF (.NET 8), the Google Sheets API, and WebView2.

## Features

- **Dashboard** — active disciplinaries (computed from expiry dates), outstanding
  course counts, and the platoon ORBAT at a glance.
- **Attendance** — the configured platoon tab as clickable cells. Click to cycle
  Present → LOA → Late → AWOL → Excused (matching the sheet's colour legend),
  then Save writes the colours back to the real sheet.
- **Platoon Attendance** — read-only website attendance history for the configured
  platoon, combined into monthly platoon-wide matrices. The app discovers HQ and
  section links dynamically from the authenticated tracker instead of relying on
  its internal section IDs.
- **Courses** — the Section Courses matrix as a colour-coded grid with a
  "who still needs X" filter.
- **Campaign Medals** — finds the highest medal currently earned but not yet
  recorded for each soldier in the configured platoon, cross-checked against
  the live SuT ORBAT.
- **CEFO** — read-only loadout cards per role, searchable.
- **Training Reports** — scans the complete unit training-report archive,
  filters it to the configured platoon, and shows 1/2/3 Section submissions
  per PDT date.
- **In-app updates** — checks the project's public GitHub Releases, verifies the
  downloaded executable with SHA-256, replaces the running copy, and restarts
  without touching settings, credentials, browser sessions, or todo data.
- **Sheets (browser)** — any other sheet opens in an embedded WebView2 tab
  inside the app, with a persistent Google login.

## One-time setup

### 1. Install prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) —
  already present on Windows 11 / recent Windows 10.
- Visual Studio 2022 (Community is fine, select the ".NET desktop development"
  workload) or VS Code with the C# Dev Kit.

### 2. Create Google API credentials (~15 min, free)

1. Go to <https://console.cloud.google.com> and sign in with the Google account
   that has access to the unit sheets.
2. Create a new project (name it anything, e.g. `4thib-tracker`).
3. **APIs & Services → Library** → search "Google Sheets API" → **Enable**.
4. **APIs & Services → OAuth consent screen** → External → fill in the app name
   and your email → add yourself as a **test user**. (Stays in testing mode —
   that's fine for personal use.)
5. **APIs & Services → Credentials → Create credentials → OAuth client ID** →
   Application type: **Desktop app**.
6. Download the JSON. In the app, open **Settings → Google Access** and choose
   **Import / replace credentials.json**. For development, placing a file named
   `credentials.json` in `src/4thIBTracker/` also works as a one-time migration
   source.

The imported client file and the authorisation token are stored in
`%APPDATA%\4thIBTracker`. The first sheet load opens a browser asking you to
authorise the app; you won't be asked again unless the credentials are replaced.

`credentials.json` and the token identify *you* — don't commit them or share them.

### 3. Point the app at your sheets

Use the app's **Settings** page. During development, you can instead edit
`src/4thIBTracker/appsettings.json` before the first launch. The editable
runtime copy is stored at `%APPDATA%\4thIBTracker\appsettings.json`:

- For each entry under `Spreadsheets`, paste the spreadsheet ID — the long
  string in the sheet's URL between `/d/` and `/edit`.
  - `Attendance` → Unit Attendance sheet
  - `Discipline` → the platoon's discipline tracker
  - `SectionCourses` → the Section Courses tracker; the matching platoon tab is
    selected automatically from the configured platoon number
  - `Cefo` → the master CEFO workbook
  - `CampaignMedalOutcomes` and `CampaignMedalAwards` → the accumulated campaign
    medals workbook's `Outcomes` and `Accum Medals` tabs
- Under `BrowserTabs`, paste full URLs for any sheets you want as embedded tabs.
- `Forum.TrainingReportsForumUrl` points at the unit-wide Training Reports
  archive. Its page count is discovered automatically, so it does not need to
  be updated as new archive pages are added.
- Tab names must match the configured sheet's tab names exactly.

### 4. Build and run

```
cd 4thIBTracker
dotnet run --project src/4thIBTracker
```

Or open `4thIBTracker.sln` in Visual Studio and press F5.

### 5. Single-exe build (optional)

Run `publish.cmd`. The checked publisher produces `publish\4thIBTracker.exe`
and its SHA-256 checksum. The executable is self-contained and runs on any
64-bit Windows machine without .NET installed. Users need only the executable;
no configuration or credential sidecars are required. A fresh user imports
their Google OAuth JSON from the Settings page.

Editable platoon settings are stored at
`%APPDATA%\4thIBTracker\appsettings.json`. On the first launch after upgrading
from an older version, the app copies the existing `appsettings.json` from next
to the exe into that per-user location. Future upgrades can therefore replace
only `4thIBTracker.exe` without overwriting the user's settings. New settings
introduced by an update are merged in while all existing user values are kept.
An existing sidecar `credentials.json` is likewise copied once to the per-user
directory and is never overwritten by an executable upgrade.

The exe is large (~150 MB) because it carries the whole .NET runtime. If the
machine already has the .NET 8 Desktop Runtime, swap
`--self-contained true` for `--self-contained false` to get a small exe instead.

## Publishing updates

GitHub Actions builds and publishes releases automatically. The repository must
be public because installed apps read the public Releases API without storing a
GitHub token.

1. Commit and push the finished changes to `main`.
2. Create a three-part version tag and push it:

   ```powershell
   git tag v1.0.0
   git push origin main --tags
   ```

3. The `Publish release` workflow validates the neutral configuration, builds
   the self-contained executable, creates its checksum, and attaches both files
   to a GitHub Release.

The workflow embeds its own `owner/repository` identifier in the executable.
That means no repository URL is hard-coded in source, while installed release
builds still know where to check. The first updater-enabled executable must be
distributed normally; every later higher version can install itself from inside
the app. Release tags and project versions use `MAJOR.MINOR.PATCH` format.

For a checked local build, run:

```powershell
./scripts/Publish-Release.ps1 -Version 1.0.0 -Repository owner/repository
```

The local publisher refuses to run if credentials are present or embedded
defaults contain unit-specific IDs, tabs, URLs, names, or browser links.

## Project layout

```
src/4thIBTracker/
  Models/Models.cs             # Disciplinary, CourseRecord, AttendanceRow, CefoRole
  Services/AppConfig.cs        # appsettings.json loader
  Services/UpdateService.cs    # GitHub release check, verification and replacement
  Services/GoogleSheetsService.cs  # OAuth + value/colour reads, colour writes
  Services/SheetParsers.cs     # sheet-layout knowledge lives HERE
  ViewModels/ViewModels.cs     # one VM per view (CommunityToolkit.Mvvm)
  Views/                       # Dashboard, Attendance, Courses, Cefo
  MainWindow.xaml(.cs)         # sidebar navigation + WebView2 tab cache
```

Repository automation lives under `.github/`; the checked release publisher is
`scripts/Publish-Release.ps1`.

## When a sheet's layout changes

All row/column assumptions live in `Services/SheetParsers.cs` (each parser has a
comment describing the expected layout). If someone restructures a sheet and a
view goes wrong, fix the constants there.

## Ideas for later

- Discipline manager view (append rows via `AppendRowAsync` — already in the service)
- Logistics order form writing to the section Logi tabs
- NCO course tracker monthly tick view
- "Changed since last look" badges using the Drive API revisions feed
