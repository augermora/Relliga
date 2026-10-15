namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    // Measured provenance scans dominated JSON export and repeated integrity checks.
    // These additive indexes preserve every row and identity in version 1/2 libraries.
    private const string SchemaV3 = """
        CREATE INDEX ix_event_origins_event ON event_origins(event_id,operation_id,source_file,record_index);
        CREATE INDEX ix_calendar_origins_component ON calendar_origins(component_id,operation_id);
        CREATE INDEX ix_events_provider_date ON events(provider,ended_at DESC,id DESC);
        PRAGMA user_version=3;
        """;
}
