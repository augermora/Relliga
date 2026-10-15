# Working on Relliga

- Keep Domain independent of frameworks and providers. Application owns use cases and contracts; Infrastructure implements persistence; Importers adapt export formats; Desktop owns UI and composition.
- Use .NET 10, async I/O, cancellation, dependency injection, CommunityToolkit MVVM, and stable GPLv3-compatible dependencies. Avoid speculative abstractions.
- Keep user data outside the repository in the per-user application directory. Never add real exports, databases, credentials, telemetry, accounts, or runtime network calls.
- Treat imports as untrusted. Enforce byte, record, depth, entry and expansion limits. Never extract ZIP members to disk. Use parameterized SQL and transactional writes.
- Preserve nullable source fields, original metadata and provenance. Changes to deduplication must preserve distinct occurrences and include regression tests. Do not silently discard records or exceptions.
- Add sequential schema migrations; reject future schemas. Do not delete or recreate an existing user library to resolve a migration failure.
- All visible UI controls must work. Keep heavy parsing and SQLite operations off the UI thread. Show loading, cancellation, warnings, and actionable errors. Preserve keyboard access and readable contrast.
- Use synthetic fixtures only. Test parser failures, security limits, persistence, rollback, cancellation, search and deduplication when changing those behaviors.
- Before delivery run `dotnet restore`, `dotnet build`, and `dotnet test`. The repository opts into Microsoft Testing Platform in global.json. For a named solution use `dotnet test --solution Relliga.slnx`. Report unverified GUI or platform behavior honestly.
- Preserve LICENSE and Git configuration. Do not commit, push, publish, or modify GitHub settings without user approval.
