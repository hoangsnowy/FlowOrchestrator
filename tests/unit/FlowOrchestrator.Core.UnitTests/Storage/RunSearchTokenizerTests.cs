using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// <see cref="RunSearchTokenizer"/> decides what the opt-in token index can find: identifiers stay whole,
/// their parts stay findable, prose and arrays inside JSON are skipped.
/// </summary>
public sealed class RunSearchTokenizerTests
{
    private static HashSet<string> Text(string text, bool camelCase = false, int max = RunSearchTokenizer.DefaultMaxTokensPerDocument)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        RunSearchTokenizer.AddText(text, set, camelCase, max);
        return set;
    }

    private static HashSet<string> Json(string json, int max = RunSearchTokenizer.DefaultMaxTokensPerDocument)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        RunSearchTokenizer.AddJsonValues(json, set, max);
        return set;
    }

    [Fact]
    public void Hyphenated_business_id_is_one_token_plus_its_parts()
    {
        // Arrange

        // Act
        var tokens = Text("Shipment ORD-2026-0193334 failed");

        // Assert
        Assert.Contains("ord-2026-0193334", tokens);
        Assert.Contains("ord", tokens);
        Assert.Contains("2026", tokens);
        Assert.Contains("0193334", tokens);
        Assert.Contains("shipment", tokens);
        Assert.Contains("failed", tokens);
    }

    [Fact]
    public void Email_keeps_its_joiners_and_trailing_punctuation_is_trimmed()
    {
        // Arrange

        // Act
        var tokens = Text("notify jane.doe@example.com.");

        // Assert
        Assert.Contains("jane.doe@example.com", tokens);
        Assert.Contains("example", tokens);
        Assert.DoesNotContain("jane.doe@example.com.", tokens);
    }

    [Fact]
    public void CamelCase_humps_are_split_only_when_asked()
    {
        // Arrange

        // Act
        var names = Text("InvoiceSyncFlow", camelCase: true);
        var plain = Text("InvoiceSyncFlow");

        // Assert
        Assert.Contains("invoicesyncflow", names);
        Assert.Contains("invoice", names);
        Assert.Contains("sync", names);
        Assert.Contains("flow", names);
        Assert.Equal(new[] { "invoicesyncflow" }, plain);
    }

    [Fact]
    public void Guid_is_one_token_without_its_groups()
    {
        // Arrange
        var id = Guid.NewGuid().ToString("D");

        // Act
        var tokens = Text(id);

        // Assert
        Assert.Equal(new[] { id }, tokens);
    }

    [Fact]
    public void Single_characters_are_not_tokens()
    {
        // Arrange

        // Act
        var tokens = Text("a b c 7");

        // Assert
        Assert.Empty(tokens);
    }

    [Fact]
    public void Json_indexes_scalar_identifier_values_only()
    {
        // Arrange
        const string json = """
            { "orderId": "ORD-1", "note": "customer asked for express shipping",
              "lines": [ { "sku": "SKU-ARRAY" } ], "count": 4, "nested": { "invoice": "INV-77" } }
            """;

        // Act
        var tokens = Json(json);

        // Assert
        Assert.Contains("ord-1", tokens);
        Assert.Contains("inv-77", tokens);
        Assert.DoesNotContain("express", tokens);      // prose inside a value
        Assert.DoesNotContain("sku-array", tokens);    // array element
        Assert.DoesNotContain("orderid", tokens);      // property names are not values
    }

    [Fact]
    public void Invalid_json_keeps_what_was_read_and_does_not_throw()
    {
        // Arrange
        const string json = """{ "a": "TOKEN-1", "b": """;

        // Act
        var tokens = Json(json);

        // Assert
        Assert.Contains("token-1", tokens);
    }

    [Fact]
    public void Token_cap_bounds_the_set()
    {
        // Arrange
        var text = string.Join(' ', Enumerable.Range(0, 100).Select(i => "word" + i));

        // Act
        var tokens = Text(text, max: 10);

        // Assert
        Assert.Equal(10, tokens.Count);
    }

    [Fact]
    public void Over_long_values_are_skipped()
    {
        // Arrange
        var json = "{\"blob\":\"" + new string('x', 200) + "\",\"id\":\"ID-9\"}";

        // Act
        var tokens = Json(json);

        // Assert
        Assert.Contains("id-9", tokens);
        Assert.DoesNotContain(tokens, t => t.StartsWith('x'));
    }

    [Fact]
    public void QueryTokens_are_distinct_lower_cased_and_longest_first()
    {
        // Arrange

        // Act
        var tokens = RunSearchTokenizer.QueryTokens("Deadlock victim DEADLOCK v");

        // Assert
        Assert.Equal(new[] { "deadlock", "victim" }, tokens);
    }

    [Fact]
    public void QueryTokens_keep_only_the_longest_ones()
    {
        // Arrange

        // Act
        var tokens = RunSearchTokenizer.QueryTokens("aa bbb cccc ddddd eeeeee", maxTokens: 2);

        // Assert
        Assert.Equal(new[] { "eeeeee", "ddddd" }, tokens);
    }
}
