using IntuneWipePortal.Services;
using Xunit;

namespace IntuneWipePortal.Tests;

public class BulkImportParserTests
{
    [Fact]
    public void Parse_NullOrWhitespace_ReturnsEmpty()
    {
        var result = BulkImportParser.Parse("   ");
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void Parse_SingleColumn_UsesGuidAsNameAndEntraId()
    {
        var guid = Guid.NewGuid();
        var result = BulkImportParser.Parse(guid.ToString());

        var row = Assert.Single(result.Rows);
        Assert.Null(row.Error);
        Assert.Equal(guid, row.EntraDeviceId);
        Assert.Equal(guid.ToString(), row.DeviceName);
        Assert.Equal(1, result.ValidCount);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndBlankLines()
    {
        var guid = Guid.NewGuid();
        var input = $"# this is a comment\n\n{guid}\n  \n# trailing";

        var result = BulkImportParser.Parse(input);

        Assert.Equal(1, result.Total);
        Assert.Equal(guid, Assert.Single(result.Rows).EntraDeviceId);
    }

    [Fact]
    public void Parse_HeaderRow_ResolvesColumnsByName()
    {
        var guid = Guid.NewGuid();
        var input = $"intuneDeviceId,entraDeviceId,deviceName\nINT-1,{guid},LAPTOP-01";

        var result = BulkImportParser.Parse(input);

        var row = Assert.Single(result.Rows);
        Assert.Null(row.Error);
        Assert.Equal(guid, row.EntraDeviceId);
        Assert.Equal("LAPTOP-01", row.DeviceName);
        Assert.Equal("INT-1", row.IntuneDeviceId);
    }

    [Fact]
    public void Parse_HeaderMissingEntraColumn_ReturnsHeaderError()
    {
        var input = "deviceName,intuneDeviceId\nLAPTOP-01,INT-1";

        var result = BulkImportParser.Parse(input);

        Assert.NotNull(result.HeaderError);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Parse_InvalidGuid_FlagsRowWithError()
    {
        var input = "not-a-guid";

        var result = BulkImportParser.Parse(input);

        var row = Assert.Single(result.Rows);
        Assert.NotNull(row.Error);
        Assert.Equal(0, result.ValidCount);
        Assert.Equal(1, result.InvalidCount);
    }

    [Fact]
    public void Parse_SemicolonSeparatedWithQuotedFields_IsHandled()
    {
        var guid = Guid.NewGuid();
        var input = $"\"LAPTOP; HQ\";{guid}";

        var result = BulkImportParser.Parse(input);

        var row = Assert.Single(result.Rows);
        Assert.Null(row.Error);
        Assert.Equal("LAPTOP; HQ", row.DeviceName);
        Assert.Equal(guid, row.EntraDeviceId);
    }

    [Fact]
    public void ValidInputs_ReturnsOnlyValidRows()
    {
        var guid = Guid.NewGuid();
        var input = $"{guid}\nnot-a-guid";

        var result = BulkImportParser.Parse(input);
        var valid = BulkImportParser.ValidInputs(result.Rows).ToList();

        var only = Assert.Single(valid);
        Assert.Equal(guid, only.EntraDeviceId);
    }
}
