using Relliga.Domain;

namespace Relliga.Desktop;

public sealed record ImportItem(ImportOperation Record)
{
    public string Label => $"{Record.Provider} · {Record.FileName}\n{Record.StartedAt.ToLocalTime():dd MMM yyyy HH:mm} · {Record.Status}\n{Record.Added:N0} added · {Record.Updated:N0} updated · {Record.Duplicates:N0} skipped · {Record.Warnings:N0} warnings";
    public string? Message => Record.Message;
    public string Details => $"{Record.Provider}\n{Record.FileName}\n{Record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\nStatus: {Record.Status}\nImporter: {Record.ImporterVersion}\n\nProcessed: {Count(Record.Processed)}\nImported: {Record.Added:N0}\nUpdated: {Record.Updated:N0}\nSkipped (already present/stale): {Record.Duplicates:N0}\nRejected: {Count(Record.Rejected)}\nWarnings: {Record.Warnings:N0}\n\n{Record.Message}";
    private static string Count(int? value) => value?.ToString("N0") ?? "Not recorded by version 0.1";
    public override string ToString() => Label;
}
