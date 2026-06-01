using System.Text.Json;
using MixCut.Services.AI;
using Xunit;

namespace MixCut.Tests.Services.AI;

/// <summary>
/// JsonTruncationRepair unit tests: repair / sanitize logic for AI responses truncated by max_tokens.
/// Pure string to string deterministic logic, no external dependency -- ideal first sample for the test project.
/// </summary>
public class JsonTruncationRepairTests
{
    // ============ Sanitize ============

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_NullOrEmpty_ReturnsInput(string? input)
    {
        Assert.Equal(input, JsonTruncationRepair.Sanitize(input!));
    }

    [Fact]
    public void Sanitize_KeepsNormalText()
    {
        const string json = "{\"name\":\"kangwang\",\"score\":9}";
        Assert.Equal(json, JsonTruncationRepair.Sanitize(json));
    }

    [Fact]
    public void Sanitize_KeepsTabNewlineCarriageReturn()
    {
        const string input = "{\n\t\"a\":1\r\n}";
        Assert.Equal(input, JsonTruncationRepair.Sanitize(input));
    }

    [Fact]
    public void Sanitize_RemovesControlChars()
    {
        // NUL (\u0000) and BEL (\u0007) are illegal control chars and must be stripped; visible chars kept.
        var input = "{\u0000\"a\":\u0007\"b\"}";
        var result = JsonTruncationRepair.Sanitize(input);
        Assert.Equal("{\"a\":\"b\"}", result);
    }

    [Fact]
    public void Sanitize_RemovesBom()
    {
        var input = "\uFEFF{\"a\":1}";
        var result = JsonTruncationRepair.Sanitize(input);
        Assert.Equal("{\"a\":1}", result);
    }

    // ============ Repair ============

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Repair_NullOrBlank_ReturnsInput(string? input)
    {
        Assert.Equal(input, JsonTruncationRepair.Repair(input!));
    }

    [Fact]
    public void Repair_CompleteObject_ReturnsUnchanged()
    {
        const string json = "{\"a\":1,\"b\":[1,2,3]}";
        Assert.Equal(json, JsonTruncationRepair.Repair(json));
    }

    [Fact]
    public void Repair_CompleteArray_ReturnsUnchanged()
    {
        const string json = "[{\"a\":1},{\"b\":2}]";
        Assert.Equal(json, JsonTruncationRepair.Repair(json));
    }

    [Fact]
    public void Repair_ArrayTruncatedMidObject_CutsBackToLastCompleteObjectAndCloses()
    {
        // 2nd object truncated mid-way -> keep the 1st complete object + re-close the array ]
        const string truncated = "[{\"id\":1,\"name\":\"a\"},{\"id\":2,\"name\":\"b";
        var repaired = JsonTruncationRepair.Repair(truncated);

        var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(repaired);
        Assert.NotNull(parsed);
        Assert.Single(parsed!);
        Assert.Equal(1, parsed![0]["id"].GetInt32());
    }

    [Fact]
    public void Repair_ArrayTruncatedAfterCompleteObject_ClosesArray()
    {
        const string truncated = "[{\"id\":1},{\"id\":2},";
        var repaired = JsonTruncationRepair.Repair(truncated);

        var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(repaired);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
    }

    [Fact]
    public void Repair_NoSafeCutPoint_ReturnsInputUnchanged()
    {
        // Even the first object is incomplete -> no safe cut point -> give up, return as-is.
        const string truncated = "[{\"id\":1,\"name\":\"unfinished";
        Assert.Equal(truncated, JsonTruncationRepair.Repair(truncated));
    }

    [Fact]
    public void Repair_BracesInsideStringValue_NotMistakenAsStructure()
    {
        // } ] inside a string value must not be treated as structural brackets -- complete JSON returned as-is.
        const string json = "[{\"text\":\"a]}b{[\"},{\"text\":\"ok\"}]";
        Assert.Equal(json, JsonTruncationRepair.Repair(json));
    }

    [Fact]
    public void Repair_OutputIsAlwaysParseableOrUnchanged()
    {
        // Truncated inside a nested object: result must be either the original or valid parseable JSON.
        const string truncated = "[{\"a\":{\"x\":1}},{\"a\":{\"x\":2},\"b\":\"cut";
        var repaired = JsonTruncationRepair.Repair(truncated);
        var ex = Record.Exception(() =>
            JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(repaired));
        Assert.Null(ex);
    }
}
