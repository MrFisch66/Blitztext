using System.Text;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;

namespace Blitztext.Core.Services;

public static class PromptBuilder
{
    public const string FastEditModel = "gpt-4o-mini";
    public const string RageModeModel = "gpt-4o";

    public static TextRewriteRequest BuildImproveRequest(string text, TextImprovementSettings settings)
    {
        return new TextRewriteRequest(
            RewriteOperation.Improve,
            text,
            BuildTextImprovementPrompt(settings),
            FastEditModel,
            0.3);
    }

    public static TextRewriteRequest BuildDampfAblassenRequest(string text, DampfAblassenSettings settings)
    {
        return new TextRewriteRequest(
            RewriteOperation.DampfAblassen,
            text,
            settings.SystemPrompt,
            RageModeModel,
            0.4);
    }

    public static TextRewriteRequest BuildEmojiRequest(string text, EmojiTextSettings settings)
    {
        return new TextRewriteRequest(
            RewriteOperation.AddEmojis,
            text,
            BuildEmojiSystemPrompt(settings.EmojiDensity),
            FastEditModel,
            0.3);
    }

    public static string BuildTextImprovementPrompt(TextImprovementSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            var prompt = settings.SystemPrompt.Trim();
            if (settings.CustomTerms.Count > 0)
            {
                prompt += $"{Environment.NewLine}{Environment.NewLine}Wichtig: Diese Eigennamen und Fachbegriffe müssen exakt so geschrieben werden: {string.Join(", ", settings.CustomTerms)}";
            }

            return prompt;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Du bist ein Lektor und Schreibassistent. Verbessere den folgenden Text:");
        builder.AppendLine("- Korrigiere Rechtschreibung und Grammatik");
        builder.AppendLine("- Verbessere die Formulierung und den Lesefluss");
        builder.AppendLine("- Behalte die ursprüngliche Bedeutung bei");
        builder.Append("- Gib NUR den verbesserten Text zurück, keine Erklärungen");

        builder.Append(settings.Tone switch
        {
            TextTone.Formal => $"{Environment.NewLine}- Verwende einen formellen, professionellen Ton",
            TextTone.Neutral => $"{Environment.NewLine}- Verwende einen neutralen, klaren Ton",
            TextTone.Casual => $"{Environment.NewLine}- Verwende einen lockeren, natürlichen Ton",
            _ => string.Empty
        });

        if (settings.CustomTerms.Count > 0)
        {
            builder.Append($"{Environment.NewLine}{Environment.NewLine}Wichtig: Diese Eigennamen und Fachbegriffe müssen exakt so geschrieben werden: {string.Join(", ", settings.CustomTerms)}");
        }

        if (!string.IsNullOrWhiteSpace(settings.Context))
        {
            builder.Append($"{Environment.NewLine}{Environment.NewLine}Kontext: {settings.Context.Trim()}");
        }

        return builder.ToString();
    }

    public static string BuildEmojiSystemPrompt(EmojiDensity density)
    {
        var densityInstruction = density switch
        {
            EmojiDensity.Wenig => "Setze nur vereinzelt Emojis ein, maximal 1-2 pro Absatz.",
            EmojiDensity.Mittel => "Setze regelmäßig passende Emojis ein, etwa alle 1-2 Sätze.",
            EmojiDensity.Viel => "Setze großzügig Emojis ein, gerne mehrere pro Satz.",
            _ => "Setze passende Emojis ein."
        };

        return $"Du erhältst ein gesprochenes Transkript. Gib den Text möglichst originalgetreu zurück, aber füge passende Emojis ein. {densityInstruction} Korrigiere offensichtliche Sprach- und Grammatikfehler. Behalte den Stil und die Bedeutung bei. Gib NUR den Text mit Emojis zurück, keine Erklärungen.";
    }
}
