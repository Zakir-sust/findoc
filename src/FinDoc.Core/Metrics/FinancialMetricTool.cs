using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using FinDoc.Core.Prompting;

namespace FinDoc.Core.Metrics;

/// <summary>
/// What get_financial_metric sends back to the model (C17 spec: fiscal year, report, value, unit, note, item;
/// plus the company, the exact citation label and, when nothing matches, a message).
/// </summary>
public sealed record MetricResult(
    bool Found,
    string Company,
    string Metric,
    int FiscalYear,
    decimal? Value = null,
    string? Unit = null,
    string? Report = null,
    string? Item = null,
    string? Citation = null,
    string? Note = null,
    string? Message = null)
{
    internal static MetricResult From(MetricRow row) =>
        new(true, row.Company, row.Metric, row.FiscalYear, row.Value, row.Unit, row.Report, row.Item,
            CitationPrompt.LabelFor(row.Report, row.Item), row.Note);

    // Same answer for an unknown company, an unknown metric, a year not in the table and a row with no value.
    // No note either: Amazon's R&D note quotes a tax figure (22,701) the model could take for the answer.
    internal static MetricResult NotFound(string company, string metric, int fiscalYear) =>
        new(false, company, metric, fiscalYear, Message: $"No data for {company} {metric} in fiscal {fiscalYear}.");
}

/// <summary>
/// The get_financial_metric tool (C17). The model asks for it with a name and JSON arguments; the
/// FunctionInvokingChatClient layer (ChatClientSetup) runs this method, adds the result to the messages
/// and calls the model again, which writes the answer from it.
/// </summary>
public sealed class FinancialMetricTool
{
    public const string Name = "get_financial_metric";

    // The description is a prompt: the model reads it to decide when to call the tool and what to pass.
    // Jakir's wording (C17 spec); the lists must match the companies and metric names in data/metrics.json.
    public const string Description =
        "Use when a user asks for a company's metric. Metric: revenue, net_income, rnd_expense, employees. Company: Apple, Alphabet, Amazon, Tesla.";

    /// <summary>
    /// How the result is written for the model. Null fields are left out, so a "not found" carries no value,
    /// unit or note to quote. The base is MEAI's default (camelCase), which the endpoint also reads it back with;
    /// that default is indented, and every space is a prompt token, so it's written on one line.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(AIJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly MetricsTable _table;
    private readonly ILogger<FinancialMetricTool> _logger;

    public FinancialMetricTool(MetricsTable table, ILogger<FinancialMetricTool> logger)
    {
        _table = table;
        _logger = logger;
        // The JSON schema the model sees comes from this method's parameters: three, all required
        // (an int with no default can't be left out, so the model must name a fiscal year).
        Function = AIFunctionFactory.Create(GetFinancialMetric, Name, Description, JsonOptions);
    }

    /// <summary>The tool as the model sees it: name, description, parameter schema. Goes into ChatOptions.Tools.</summary>
    public AIFunction Function { get; }

    public MetricResult GetFinancialMetric(string company, string metric, int fiscalYear)
    {
        var row = _table.Find(company, metric, fiscalYear);
        var result = row is { Value: not null } ? MetricResult.From(row) : MetricResult.NotFound(company, metric, fiscalYear);

        // The proof that the model called the tool (C17 spec), with what came back.
        _logger.LogInformation("Tool {Tool} called: company={Company}, metric={Metric}, fiscalYear={FiscalYear} -> {Outcome}",
            Name, company, metric, fiscalYear, result.Found ? $"{result.Value} {result.Unit}, {result.Citation}" : "not found");

        return result;
    }
}
