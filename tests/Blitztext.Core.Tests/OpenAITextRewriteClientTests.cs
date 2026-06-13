using Blitztext.Core.Services;

namespace Blitztext.Core.Tests;

public sealed class OpenAITextRewriteClientTests
{
    [Fact]
    public void ExtractOutputText_ReadsResponsesOutputTextShortcut()
    {
        const string json = """{"output_text":"Hallo Welt"}""";

        Assert.Equal("Hallo Welt", OpenAITextRewriteClient.ExtractOutputText(json));
    }

    [Fact]
    public void ExtractOutputText_ReadsResponsesOutputItems()
    {
        const string json = """
        {
          "output": [
            {
              "type": "message",
              "content": [
                { "type": "output_text", "text": "Hallo" },
                { "type": "output_text", "text": "Welt" }
              ]
            }
          ]
        }
        """;

        Assert.Equal($"Hallo{Environment.NewLine}Welt", OpenAITextRewriteClient.ExtractOutputText(json));
    }
}
