using System.Text;
using System.Text.Json;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.UnitTests;

/// <summary>
/// <see cref="WebhookRequestBuffer"/> reads <c>application/x-www-form-urlencoded</c> bodies into a JSON
/// object for the trigger and a field map for the signature verifier (Twilio).
/// </summary>
public sealed class WebhookRequestBufferFormTests
{
    private static HttpRequest Request(string body, string contentType)
    {
        var http = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentLength = bytes.Length;
        http.Request.ContentType = contentType;
        return http.Request;
    }

    [Fact]
    public async Task Form_body_is_parsed_into_json_object_and_field_map()
    {
        // Arrange
        var request = Request("Body=hi+there&From=%2B15550001111", "application/x-www-form-urlencoded; charset=utf-8");

        // Act
        var buffered = await WebhookRequestBuffer.ReadAsync(request, 1024);

        // Assert
        Assert.Equal(WebhookRequestBuffer.BufferStatus.Ok, buffered.Status);
        var body = Assert.IsType<JsonElement>(buffered.Parsed);
        Assert.Equal("hi there", body.GetProperty("Body").GetString());
        Assert.Equal("+15550001111", body.GetProperty("From").GetString());
        Assert.NotNull(buffered.FormFields);
        Assert.Equal("hi there", buffered.FormFields!["Body"]);
        Assert.Equal("Body=hi+there&From=%2B15550001111", Encoding.UTF8.GetString(buffered.Bytes));
    }

    [Fact]
    public async Task Json_body_has_no_form_fields()
    {
        // Arrange
        var request = Request("{\"a\":1}", "application/json");

        // Act
        var buffered = await WebhookRequestBuffer.ReadAsync(request, 1024);

        // Assert
        Assert.Equal(WebhookRequestBuffer.BufferStatus.Ok, buffered.Status);
        Assert.Null(buffered.FormFields);
        Assert.IsType<JsonElement>(buffered.Parsed);
    }

    [Fact]
    public async Task Form_body_over_the_field_count_limit_is_invalid_form()
    {
        // Arrange
        var body = string.Join('&', Enumerable.Range(0, 1100).Select(i => "k" + i + "=v"));
        var request = Request(body, "application/x-www-form-urlencoded");

        // Act
        var buffered = await WebhookRequestBuffer.ReadAsync(request, 1_000_000);

        // Assert
        Assert.Equal(WebhookRequestBuffer.BufferStatus.InvalidForm, buffered.Status);
    }
}
