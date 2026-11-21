using System.Text.Json;

namespace FinDoc.Core.Metrics;

/// <summary>One row of <c>data/metrics.json</c>: one company's metric for the fiscal year of its latest 10-K (checked in C16).</summary>
/// <param name="Value">Null when the report has no such line (Amazon reports no R&amp;D expense line).</param>
public sealed record MetricRow(
    string Company, string Report, int FiscalYear, string Metric, decimal? Value, string Unit, string? Item, int? Page, string? Note);

/// <summary>
/// The checked metrics table, loaded once at start-up. The get_financial_metric tool reads only this,
/// never numbers pulled out of chunks at question time (G37: a chunk holds several years' columns).
/// </summary>
public sealed class MetricsTable(IReadOnlyList<MetricRow> rows)
{
    public IReadOnlyList<MetricRow> Rows { get; } = rows;

    public static MetricsTable Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"The metrics table is missing: {path}", path);

        // The file's "_status", "_shape" etc. are notes for people; only "rows" is read.
        var file = JsonSerializer.Deserialize<MetricsFile>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"The metrics table is empty: {path}");
        return new MetricsTable(file.Rows);
    }

    /// <summary>The row for this company, metric and fiscal year, or null. Company and metric match in any case.</summary>
    public MetricRow? Find(string company, string metric, int fiscalYear) =>
        Rows.FirstOrDefault(r =>
            string.Equals(r.Company, company.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Metric, metric.Trim(), StringComparison.OrdinalIgnoreCase) &&
            r.FiscalYear == fiscalYear);

    private sealed record MetricsFile(List<MetricRow> Rows);
}
