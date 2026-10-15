# Relliga

**Your digital life, together.**

Relliga is a local-first, open-source desktop application for preserving and exploring personal data exports. Your history stays on your device: no account, telemetry, cloud storage, or external API calls during normal use.

## Implemented in 0.2.0

- Import Spotify Extended Streaming History JSON and Google Calendar ICS, standalone or together inside ZIP archives. Formats are detected from contents, including archive members with arbitrary filenames.
- Preview sampled records and warnings, cancel processing, and review results in a paged import history with source, date, version, status, processed/imported/updated/skipped/rejected counters, and diagnostics.
- Preserve listening events, Calendar definitions, original metadata, source filenames, record indexes, revisions and import provenance in SQLite.
- Browse a unified timeline in pages of 50, filter by source/type/date, and inspect playback or Calendar details. SQLite FTS5 searches music fields and Calendar titles/descriptions/locations.
- Handle all-day, UTC, floating and recognized timezone-aware Calendar events, bounded recurrence, recurrence dates, exclusions and individual overrides.
- Reimport overlapping exports using stable playback and Calendar identities while preserving repeated listening occurrences.
- View per-source counts, playback hours, recurring-series counts, successful imports and the most recent operation in the dark Avalonia interface.
- Recover interrupted imports, reject concurrent app access to the same library, and roll back fatal failures and cancellation.

## Stack and architecture

C# / .NET 10, Avalonia 12.1.4 with Fluent theme and Inter typography, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection, Microsoft.Data.Sqlite / SQLite FTS5, [Ical.Net 5.2.3](https://github.com/ical-org/ical.net) (MIT), NodaTime 3.3.5 (Apache-2.0), and xUnit v3 with Microsoft Testing Platform.

| Project | Responsibility |
| --- | --- |
| `src/Relliga.Domain` | Provider-independent event and import models |
| `src/Relliga.Application` | Import orchestration and service contracts |
| `src/Relliga.Infrastructure` | SQLite migrations, transactions, provenance and queries |
| `src/Relliga.Importers` | Content routing, Spotify/Calendar parsers, recurrence evaluation and archive validation |
| `src/Relliga.Desktop` | Avalonia views, MVVM state and dependency composition |
| `tests/Relliga.Tests` | Synthetic parser and SQLite integration tests |

Domain and Application do not depend on Avalonia, SQLite or Ical.Net. Infrastructure queries use the application-owned recurrence contract; the desktop composition root supplies its implementation. Both sources share import orchestration, querying, paging, history and UI navigation.

## Development and Windows launch

Install the **.NET 10 SDK**. A graphical desktop is required to run the application; package restore needs access to NuGet. No separate SQLite installation is required.

From PowerShell:

```powershell
Set-Location C:\Dev\Relliga
dotnet restore
dotnet build --no-restore
dotnet test --no-build --no-restore
dotnet run --project .\src\Relliga.Desktop\Relliga.Desktop.csproj --no-build
```

After a Debug build, you can also run `C:\Dev\Relliga\src\Relliga.Desktop\bin\Debug\net10.0\Relliga.Desktop.exe` directly with the .NET 10 runtime installed. The adjacent build output files are required. No installer is included yet.

The source targets Windows, macOS and Linux through Avalonia. Native desktop dependencies and platform testing are still required on macOS/Linux; this iteration is validated on Windows only. If a restricted build environment blocks MSBuild worker processes, use `dotnet build --no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false`. The test runner requires local named-pipe access.

For isolated development or GUI testing, set `RELLIGA_DATA_DIRECTORY` to a temporary directory outside the repository before launching. Leave it unset for the normal per-user library. Settings displays the actual library path.

## Importing Spotify data

Request **Extended streaming history** from Spotify's privacy controls. See Spotify's [Understanding your data](https://support.spotify.com/article/understanding-your-data/) for the fields included and the technical README delivered with each export.

Choose **Import data → Choose file**, select a JSON file or ZIP, review the detected source, then choose **Import into library**. Open Timeline or Search after import. Import separate JSON files one at a time, or select the archive containing them.

The supported format is a JSON array of extended streaming records with `ts` (stream end time with UTC offset), non-negative integer `ms_played`, and Spotify identity fields such as `master_metadata_track_name`, `spotify_track_uri`, or `episode_name`. Track title, artist, album, URI and podcast fields are used when present; missing fields remain null. Other exported fields remain in the local metadata JSON. Standard account history with `endTime` / `msPlayed`, playlists and technical logs are unsupported.

File names do not determine source detection. ZIP members are inspected for supported content. Unsupported content is reported as skipped. Invalid members produce explicit warnings; any valid JSON records already decoded from that member are retained and other members continue. Calendar documents require valid component boundaries before their events are accepted. A standalone syntax error, unsafe archive, I/O failure or cancellation rolls back the operation. Wholly empty/unsupported input fails without saving events. Up to 100 diagnostic examples and the full warning count are retained. Unparsed archive members are reported separately; their unknown record counts are never invented.

Safety limits: 64 MiB per JSON/member, 256 MiB archive and total expanded content, 256 ZIP entries, 200:1 maximum compression ratio, 1,000,000 records, JSON depth 32, 1 MiB characters per record and 64 KiB per normalized string. Actual expanded bytes are checked while reading. Unsafe paths and duplicate archive member names are rejected. ZIP members are never extracted.

## Google Calendar and recurrence

Import an exported `.ics` file or a ZIP containing calendars. Compatible iCalendar 2.0 VEVENT exports are supported; no Google account connection is needed. Titles, descriptions, locations, start/end values, timezone IDs, UID, recurrence identity, sequence and export metadata are retained. Missing UIDs are rejected rather than replaced with generated IDs. Attachments, URLs and alarms remain passive metadata and are never fetched or executed.

Calendar definitions are stored once per **UID + RECURRENCE-ID**. Occurrences are evaluated only for a query window of **at most 366 days**, with a default 90-day window when including Calendar. Explicit date filters apply to both sources; the UI displays the effective window. Select Spotify to browse playback history without the Calendar window limit. Dates are inclusive in the UI, with an exclusive upper bound internally; filtering uses occurrence start dates rather than interval overlap.

Supported recurrence includes DAILY, WEEKLY, MONTHLY and YEARLY RRULEs, COUNT/UNTIL, common date selectors, RDATE (including periods), EXDATE, moved individual occurrences and cancellation components. Finite counts exclude RRULE-generated nonexistent DST times before exclusions are applied. Ambiguous explicit local times use the earlier offset; explicit gap values use the pre-transition offset. All-day events remain dates with exclusive end dates. Floating clocks retain no assigned timezone or UTC instant. Timed events sort/display in UTC; all-day dates and floating clocks use their exported date/clock for neutral timeline placement, which cannot establish an absolute instant.

**Explicit limitations:** subdaily recurrence frequencies, BYHOUR/BYMINUTE/BYSECOND expansion, multiple RRULEs, EXRULE and RECURRENCE-ID RANGE/THISANDFUTURE are rejected with diagnostics. Invalid components block other components of the same series in that input. Recognized timezone IDs use the bundled timezone database. Embedded VTIMEZONE definitions are preserved with a warning; custom timezone rules are not evaluated. Import limits are 16 MiB per ICS document, 10,000 components per document, nesting depth 32, and bounded text/date-selector lengths. Queries enforce definition, occurrence, memory and evaluation budgets and fail clearly rather than returning silently truncated results. Very large or pathological series can require narrower windows or simpler definitions.

Newer Calendar SEQUENCE values, or newer timestamps at the same sequence, update the definition. Older revisions are skipped; differing revisions with no ordering evidence retain the existing definition and produce a conflict warning. Incoming revisions are retained in provenance. Absence from a later partial export does not delete an earlier event or override; an explicit cancellation is required.

## Persistence, privacy and limitations

- Windows stores the library at `%LOCALAPPDATA%\Relliga\relliga.db`; other platforms use .NET's per-user LocalApplicationData location. Application data is outside the source checkout. SQLite uses WAL, foreign keys, versioned transactional migrations and indexed queries. Schema version 2 upgrades version 1 libraries without deleting rows, provenance or original importer versions.
- The library is **not encrypted at rest**. Full export metadata may include Spotify usernames, IP addresses or other sensitive information. Protect your device and backups. Original source files are read without modification.
- Close Relliga before backing up its data directory; preserve the database together with any SQLite sidecar files. Backup, restore and deletion controls are not included in this release.
- Playback identity uses media URI, UTC end instant, played duration, account and device, plus multiplicity within the selected input. Title, album, IP and similar metadata changes no longer create new rows when this identity is available. Without a URI, canonical JSON remains the conservative fallback. Spotify exports contain no universal playback event ID: two physically distinct plays with identical identity fields in separate exports remain indistinguishable. Multiplicity is preserved up to the highest observed count, and device/account differences remain distinct. Historical rows already duplicated by version 0.1 are preserved rather than destructively merged during migration. Incoming metadata remains in provenance.
- Preview samples at most 1,000 valid events and does not guarantee the remainder of the input is valid. Progress is record-based feedback rather than a percentage. Large imports still require a bounded in-memory dictionary of event fingerprints.
- Search matches Unicode word prefixes, combining terms with AND. It is not arbitrary substring or advanced query syntax. History is paged in groups of 20; version 0.1 operations display unknown processed/rejected counters honestly. Dashboard Calendar counts represent logical events/series, not an infinite total of recurring occurrences; import counters count decoded components.
- Spotify and Calendar are implemented. There is no live synchronization, encryption, installer, localization or automatic update service yet. Full screen-reader auditing and macOS/Linux GUI verification remain outstanding.

## Test both sources on Windows without personal data

For a fresh test library, set this in PowerShell before launching:

```powershell
$env:RELLIGA_DATA_DIRECTORY = Join-Path $env:TEMP ('Relliga-Synthetic-' + [guid]::NewGuid().ToString('N'))
dotnet run --project C:\Dev\Relliga\src\Relliga.Desktop\Relliga.Desktop.csproj --no-build
```

1. Import `C:\Dev\Relliga\tests\Relliga.Tests\Fixtures\synthetic-spotify.json`.
2. Import `C:\Dev\Relliga\tests\Relliga.Tests\Fixtures\synthetic-calendar.ics`.
3. In Timeline choose All sources / All event types, set dates to `2025-01-01` through `2025-01-31`, then Apply. Expect **7 timeline occurrences**: 2 plays, an all-day event, a floating reminder and 3 planning occurrences with an exclusion and moved instance.
4. Inspect the moved event on January 12. Search `studio` with the same dates to find 3 Calendar occurrences; search `Aurora` to find the Spotify play. Exercise source/type filters and Import history.
5. Reimport both files: expect zero additional records. Dashboard should show **5 plays + logical Calendar events/series**, 2 sources, and real import counts. Four Calendar components include an override belonging to an existing series.

Close the test app and unset `RELLIGA_DATA_DIRECTORY` in that shell to return to the normal library. Both fixtures contain synthetic data only.

## Tests

`dotnet test` runs synthetic parser/security tests and real temporary SQLite integration tests, including normalization, nulls, malformed input, ZIP safety, persistence, FTS/date/paging queries, repeated imports, rollback, cancellation, schema checks and interrupted-import recovery. No real personal information is used in fixtures.

The expanded suite covers ICS validation, all-day/floating/UTC/zoned values, DST transitions, recurrence rules/dates/exclusions/overrides, overlapping imports, Spotify identity, mixed ordering/search/filters, paged history/statistics, version 1 migration, cancellation, rollback and larger collections. Windows checks use an isolated synthetic library and cover dashboard totals, unified timeline scrolling, source filters, moved-event details, Calendar search and history counters. The native picker opens with JSON/ICS/ZIP filters; a full picker-to-import GUI interaction remains unverified because native dialog entry is unreliable in the automation environment. Service and desktop-command integration tests cover actual file imports. See the delivery report for final test totals.

## License

Relliga is licensed under the **GNU General Public License, version 3**. See [LICENSE](LICENSE). Avalonia, CommunityToolkit, Ical.Net, NodaTime and the Microsoft libraries use permissive licenses; bundled native components retain their respective licenses. Relliga is not affiliated with Spotify or Google.
