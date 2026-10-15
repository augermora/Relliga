using Relliga.Domain;

namespace Relliga.Desktop;

public sealed record ImportItem(ImportOperation Record)
{
    public string Label => $"{Record.FileName} · {Record.Status} · {Record.Added:N0} added · {Record.Duplicates:N0} already present · {Record.Warnings:N0} warnings";
    public string? Message => Record.Message;
    public override string ToString() => Label;
}
