# Contributing

Use a branch for changes and keep each pull request focused. Before opening one:

1. Run `dotnet build 4thIBTracker.sln --configuration Release`.
2. Confirm `src/4thIBTracker/appsettings.json` contains only blank public defaults.
3. Never commit credentials, sheet IDs, personnel data, private URLs, generated
   CEFO exports, or browser profiles.
4. Explain the user-visible behaviour and how it was verified.

Spreadsheet and forum layouts can differ between platoons. Prefer configured
names and content-based discovery over hard-coded cells, tabs, or unit values.
