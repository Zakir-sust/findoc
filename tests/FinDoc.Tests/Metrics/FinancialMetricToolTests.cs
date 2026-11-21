using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using FinDoc.Core.Metrics;

namespace FinDoc.Tests.Metrics;

/// <summary>
/// get_financial_metric on its own: no model. It reads the real data/metrics.json (copied next to the test dll),
/// so these tests also catch a broken or renamed row in the table.
/// </summary>
public class FinancialMetricToolTests
{
    internal static readonly MetricsTable Table = MetricsTable.Load(Path.Combine(AppContext.BaseDirectory, "data", "metrics.json"));

    internal static FinancialMetricTool NewTool() => new(Table, NullLogger<FinancialMetricTool>.Instance);

    [Fact]
    public void A_row_in_the_table_comes_back_with_its_unit_item_and_citation_label()
    {
        var result = NewTool().GetFinancialMetric("Apple", "net_income", 2026);

        Assert.True(result.Found);
        Assert.Equal(132175m, result.Value);
        Assert.Equal("USD millions", result.Unit);
        Assert.Equal(("apple-fy2026", "Item 8"), (result.Report, result.Item));
        Assert.Equal("sources-[apple-fy2026]-[Item 8]", result.Citation);
    }

    [Fact]
    public void Company_and_metric_match_in_any_case()
    {
        var result = NewTool().GetFinancialMetric(" apple ", "NET_INCOME", 2026);

        Assert.True(result.Found);
        Assert.Equal("Apple", result.Company); // the table's spelling
    }

    [Theory]
    [InlineData("Apple", "net_income", 2027)]       // a year the table doesn't have (spec test question 2)
    [InlineData("Microsoft", "revenue", 2026)]      // a company the table doesn't have
    [InlineData("Apple", "operating_income", 2026)] // a metric the table doesn't have
    [InlineData("Amazon", "rnd_expense", 2025)]     // the row exists, the value is null
    public void Anything_else_is_not_found_with_no_number_to_quote(string company, string metric, int fiscalYear)
    {
        var result = NewTool().GetFinancialMetric(company, metric, fiscalYear);

        Assert.False(result.Found);
        Assert.Null(result.Value);
        Assert.Null(result.Note); // Amazon's R&D note quotes a 22,701 tax figure: never sent
        Assert.Equal($"No data for {company} {metric} in fiscal {fiscalYear}.", result.Message);
    }

    [Fact]
    public void A_not_found_result_is_written_for_the_model_without_value_unit_or_note()
    {
        var json = JsonSerializer.Serialize(NewTool().GetFinancialMetric("Apple", "net_income", 2027), FinancialMetricTool.JsonOptions);

        Assert.Equal(
            """{"found":false,"company":"Apple","metric":"net_income","fiscalYear":2027,"message":"No data for Apple net_income in fiscal 2027."}""",
            json);
    }

    [Fact]
    public void The_model_sees_one_tool_with_three_required_parameters()
    {
        var function = NewTool().Function;

        Assert.Equal("get_financial_metric", function.Name);
        Assert.Equal(FinancialMetricTool.Description, function.Description);

        var schema = function.JsonSchema;
        Assert.Equal(["company", "metric", "fiscalYear"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["company", "metric", "fiscalYear"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("fiscalYear").GetProperty("type").GetString());
    }
}
