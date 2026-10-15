# Relliga

**Your digital life, together.**

Relliga is a local-first, open-source desktop application for preserving and exploring personal data exports. Your history stays on your device: no account, telemetry, cloud storage, or external API calls during normal use.

## Implemented in 0.1.0

- Import Spotify Extended Streaming History JSON files and JSON members inside ZIP archives.
- Detect Spotify records from their contents, preview a sample, cancel processing, and review operation results and warnings.
- Preserve normalized listening events, nullable metadata, original export fields, source filenames, record indexes and importer versions in SQLite.
- Reimport the same data without adding duplicate events, while retaining identical repeated occurrences in an export.
- Browse a chronological timeline in pages of 50, filter by inclusive UTC dates, search track/artist/album text with SQLite FTS5, and inspect event details.
- View dashboard totals and recent imports in a dark Avalonia interface with native file selection.
- Recover interrupted imports, reject concurrent app access to the same library, and roll back fatal failures and cancellation.

## Stack and architecture

C# / .NET 10, Avalonia 12.1.4 with Fluent theme and Inter typography, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection, Microsoft.Data.Sqlite / SQLite FTS5, and xUnit v3 with Microsoft Testing Platform.

| Project | Responsibility |
| --- | --- |
| `src/Relliga.Domain` | Provider-independent event and import models |
| `src/Relliga.Application` | Import orchestration and service contracts |
| `src/Relliga.Infrastructure` | SQLite migrations, transactions, provenance and queries |
| `src/Relliga.Importers` | Streaming Spotify parser and archive validation |
| `src/Relliga.Desktop` | Avalonia views, MVVM state and dependency composition |
| `tests/Relliga.Tests` | Synthetic parser and SQLite integration tests |

The core does not reference Spotify or Avalonia. Future providers implement `IDataImporter` and register in the desktop composition root.

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

File names do not determine Spotify detection. ZIP discovery examines `.json` members; non-JSON members are ignored. Unsupported records produce warnings. Invalid JSON members in a ZIP produce explicit warnings and any valid records read from them before the error are retained; other members continue. A standalone JSON syntax error, unsafe archive, I/O failure or cancellation rolls back the entire operation. A wholly empty/unsupported input fails without saving events. Warning counts and up to 20 diagnostic examples are retained per successful operation.

Safety limits: 64 MiB per JSON/member, 256 MiB archive and total expanded content, 256 ZIP entries, 200:1 maximum compression ratio, 1,000,000 records, JSON depth 32, 1 MiB characters per record and 64 KiB per normalized string. Actual expanded bytes are checked while reading. Unsafe paths and duplicate archive member names are rejected. ZIP members are never extracted.

## Persistence, privacy and limitations

- Windows stores the library at `%LOCALAPPDATA%\Relliga\relliga.db`; other platforms use .NET's per-user LocalApplicationData location. Application data is outside the source checkout. SQLite uses WAL, foreign keys, a versioned transactional schema and indexed queries.
- The library is **not encrypted at rest**. Full export metadata may include Spotify usernames, IP addresses or other sensitive information. Protect your device and backups. Original source files are read without modification.
- Close Relliga before backing up its data directory; preserve the database together with any SQLite sidecar files. Backup, restore and deletion controls are not included in this release.
- Identity uses SHA-256 of canonical JSON plus occurrence number within the selected input. Object property order and whitespace do not matter. Identical records appearing multiple times are preserved up to the highest observed multiplicity. Exports that alter metadata for the same physical stream can create additional events; without a provider event ID this ambiguity cannot be resolved reliably. No missing information is invented.
- Preview samples at most 1,000 valid events and does not guarantee the remainder of the input is valid. Progress is record-based feedback rather than a percentage. Large imports still require a bounded in-memory dictionary of event fingerprints.
- Search matches Unicode word prefixes across title, artist and album, combining terms with AND. It is not arbitrary substring or advanced query syntax. The latest 20 import operations are shown; all operations remain in SQLite.
- Only Spotify is implemented. There is no live synchronization, encryption, installer, localization or automatic update service yet.

## Tests

`dotnet test` runs synthetic parser/security tests and real temporary SQLite integration tests, including normalization, nulls, malformed input, ZIP safety, persistence, FTS/date/paging queries, repeated imports, rollback, cancellation, schema checks and interrupted-import recovery. No real personal information is used in fixtures.

Validation for this iteration: restore and build succeeded with zero build warnings/errors; all 47 automated tests passed. Desktop command integration tests cover preview, import, timeline, search, inspection, reimport and error state. Windows launch, native picker opening, persisted dashboard totals, timeline, event details, search results and date-error banners were also checked in the running application using an isolated synthetic library. Automated entry into the native picker was unreliable in the verification environment, so the complete picker-to-import interaction was not independently verified through GUI automation. macOS/Linux and a full screen-reader audit remain unverified.

## License

Relliga is licensed under the **GNU General Public License, version 3**. See [LICENSE](LICENSE). Avalonia, CommunityToolkit and the Microsoft libraries use permissive licenses; bundled native components retain their respective licenses. Relliga is not affiliated with Spotify.
