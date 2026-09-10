using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace Flux.Windows;

internal static partial class MarkdownTextRenderer
{
    private static readonly MediaFontFamily CodeFont = new("Cascadia Mono, Consolas");
    private static readonly MediaBrush CodeBackground = new SolidColorBrush(MediaColor.FromArgb(38, 184, 174, 255));
    private static readonly MediaBrush MutedForeground = new SolidColorBrush(MediaColor.FromRgb(159, 165, 178));

    public static void Render(TextBlock target, string? markdown)
    {
        target.Inlines.Clear();
        var text = (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = text.Split('\n');
        var inCodeBlock = false;
        var renderedLine = false;

        foreach (var sourceLine in lines)
        {
            var trimmedStart = sourceLine.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (renderedLine)
            {
                target.Inlines.Add(new LineBreak());
            }
            renderedLine = true;

            if (inCodeBlock)
            {
                target.Inlines.Add(new Run(sourceLine)
                {
                    FontFamily = CodeFont,
                    Background = CodeBackground
                });
                continue;
            }

            var line = sourceLine;
            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                var span = new Span
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = level == 1 ? 20 : level == 2 ? 18 : 16
                };
                AddInline(span.Inlines, heading.Groups[2].Value);
                target.Inlines.Add(span);
                continue;
            }

            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                target.Inlines.Add(new Run("• ") { FontWeight = FontWeights.Bold });
                AddInline(target.Inlines, bullet.Groups[1].Value);
                continue;
            }

            var quote = QuoteRegex().Match(line);
            if (quote.Success)
            {
                target.Inlines.Add(new Run("│ ") { Foreground = MutedForeground, FontWeight = FontWeights.Bold });
                var span = new Span { Foreground = MutedForeground, FontStyle = FontStyles.Italic };
                AddInline(span.Inlines, quote.Groups[1].Value);
                target.Inlines.Add(span);
                continue;
            }

            AddInline(target.Inlines, line);
        }
    }

    private static void AddInline(InlineCollection inlines, string text)
    {
        var plain = new System.Text.StringBuilder();
        var index = 0;

        void FlushPlain()
        {
            if (plain.Length == 0)
            {
                return;
            }
            inlines.Add(new Run(plain.ToString()));
            plain.Clear();
        }

        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length && "\\`*_~[]".Contains(text[index + 1]))
            {
                plain.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (TryDelimited(text, index, "**", out var bold, out var boldEnd) ||
                TryDelimited(text, index, "__", out bold, out boldEnd))
            {
                FlushPlain();
                var span = new Bold();
                AddInline(span.Inlines, bold);
                inlines.Add(span);
                index = boldEnd;
                continue;
            }

            if (TryDelimited(text, index, "~~", out var struck, out var struckEnd))
            {
                FlushPlain();
                var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInline(span.Inlines, struck);
                inlines.Add(span);
                index = struckEnd;
                continue;
            }

            if (TryDelimited(text, index, "`", out var code, out var codeEnd))
            {
                FlushPlain();
                inlines.Add(new Run(code) { FontFamily = CodeFont, Background = CodeBackground });
                index = codeEnd;
                continue;
            }

            if ((TryDelimited(text, index, "*", out var italic, out var italicEnd) ||
                 TryDelimited(text, index, "_", out italic, out italicEnd)) &&
                !string.IsNullOrWhiteSpace(italic))
            {
                FlushPlain();
                var span = new Italic();
                AddInline(span.Inlines, italic);
                inlines.Add(span);
                index = italicEnd;
                continue;
            }

            var link = LinkRegex().Match(text, index);
            if (link.Success && link.Index == index)
            {
                FlushPlain();
                var span = new Span { TextDecorations = TextDecorations.Underline };
                AddInline(span.Inlines, link.Groups[1].Value);
                inlines.Add(span);
                index += link.Length;
                continue;
            }

            plain.Append(text[index]);
            index++;
        }

        FlushPlain();
    }

    private static bool TryDelimited(string text, int index, string marker, out string content, out int end)
    {
        content = string.Empty;
        end = index;
        if (!text.AsSpan(index).StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        var contentStart = index + marker.Length;
        var close = text.IndexOf(marker, contentStart, StringComparison.Ordinal);
        if (close <= contentStart)
        {
            return false;
        }

        content = text[contentStart..close];
        end = close + marker.Length;
        return true;
    }

    [GeneratedRegex(@"^\s*(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*[-+*]\s+(.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*>\s?(.+)$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();
}
