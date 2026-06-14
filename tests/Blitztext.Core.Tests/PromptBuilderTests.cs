using Blitztext.Core.Models;
using Blitztext.Core.Services;

namespace Blitztext.Core.Tests;

public sealed class PromptBuilderTests
{
    [Fact]
    public void BuildTextImprovementPrompt_IncludesToneContextAndCustomTerms()
    {
        var settings = new TextImprovementSettings
        {
            Tone = TextTone.Formal,
            Context = "E-Mails an Kunden",
            CustomTerms = ["Blackboat", "Blitztext"]
        };

        var prompt = PromptBuilder.BuildTextImprovementPrompt(settings);

        Assert.Contains("formellen", prompt);
        Assert.Contains("E-Mails an Kunden", prompt);
        Assert.Contains("Blackboat, Blitztext", prompt);
    }
}
