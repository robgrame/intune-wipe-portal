using System.Text;
using System.Text.Json;
using IntuneWipePortal.Models;
using IntuneWipePortal.Services;
using Xunit;

namespace IntuneWipePortal.Tests;

public class AuditExportServiceTests
{
    private static AuditEventRow SampleRow(string? deviceName = "LAPTOP-01", string? reason = "ok") =>
        new(
            Timestamp: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            EventName: "action.completed",
            CorrelationId: "corr-1",
            ActionType: "wipe",
            DeviceName: deviceName,
            IntuneDeviceId: "int-1",
            EntraDeviceId: "entra-1",
            CallerUpn: "op@contoso.com",
            Reason: reason,
            ExceptionType: null);

    [Theory]
    [InlineData("csv", AuditExportFormat.Csv)]
    [InlineData("CSV", AuditExportFormat.Csv)]
    [InlineData("xlsx", AuditExportFormat.Xlsx)]
    [InlineData("json", AuditExportFormat.Json)]
    [InlineData("unknown", AuditExportFormat.Csv)]
    [InlineData(null, AuditExportFormat.Csv)]
    public void ParseFormat_MapsKnownValues(string? value, AuditExportFormat expected)
    {
        Assert.Equal(expected, AuditExportService.ParseFormat(value));
    }

    [Fact]
    public void Export_Csv_HasBomHeaderAndRow()
    {
        var svc = new AuditExportService();
        var result = svc.Export(new[] { SampleRow() }, AuditExportFormat.Csv);

        Assert.Equal("text/csv", result.ContentType);
        Assert.Equal("csv", result.FileExtension);

        // UTF-8 BOM present
        var bom = Encoding.UTF8.GetPreamble();
        Assert.True(result.Content.Length > bom.Length);
        Assert.Equal(bom, result.Content[..bom.Length]);

        var text = Encoding.UTF8.GetString(result.Content);
        Assert.Contains("Timestamp (UTC)", text);
        Assert.Contains("action.completed", text);
        Assert.Contains("LAPTOP-01", text);
    }

    [Fact]
    public void Export_Csv_EscapesSeparatorsAndQuotes()
    {
        var svc = new AuditExportService();
        var result = svc.Export(new[] { SampleRow(deviceName: "LAP,01", reason: "say \"hi\"") }, AuditExportFormat.Csv);

        var text = Encoding.UTF8.GetString(result.Content);
        Assert.Contains("\"LAP,01\"", text);
        Assert.Contains("\"say \"\"hi\"\"\"", text);
    }

    [Fact]
    public void Export_Json_RoundTrips()
    {
        var svc = new AuditExportService();
        var result = svc.Export(new[] { SampleRow() }, AuditExportFormat.Json);

        Assert.Equal("application/json", result.ContentType);

        using var doc = JsonDocument.Parse(result.Content);
        var first = Assert.Single(doc.RootElement.EnumerateArray().ToArray());
        Assert.Equal("action.completed", first.GetProperty("EventName").GetString());
    }

    [Fact]
    public void Export_Xlsx_ProducesZipPackage()
    {
        var svc = new AuditExportService();
        var result = svc.Export(new[] { SampleRow() }, AuditExportFormat.Xlsx);

        Assert.Equal("xlsx", result.FileExtension);
        // XLSX is an OPC (ZIP) package — verify the local-file-header magic bytes.
        Assert.True(result.Content.Length > 4);
        Assert.Equal(0x50, result.Content[0]); // 'P'
        Assert.Equal(0x4B, result.Content[1]); // 'K'
    }
}
