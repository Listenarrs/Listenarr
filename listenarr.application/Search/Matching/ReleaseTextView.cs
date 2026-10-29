/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Globalization;
using System.Text;

namespace Listenarr.Application.Search.Matching;

/// <summary>A comparison view whose UTF-16 ranges always map back to original release text.</summary>
public sealed class ReleaseTextView
{
    private readonly int[] _starts;
    private readonly int[] _ends;
    public string Original { get; }
    public string Text { get; }
    public bool IsScene { get; }

    private ReleaseTextView(string original, string text, int[] starts, int[] ends, bool scene)
    {
        Original = original;
        Text = text;
        _starts = starts;
        _ends = ends;
        IsScene = scene;
    }

    public ReleaseSpan Span(int start, int length, ReleaseSpanRole role)
    {
        if (length <= 0 || start < 0 || start + length > Text.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        var rawStart = _starts[start];
        var rawEnd = _ends[start + length - 1];
        return new(rawStart, rawEnd - rawStart, role, Text.Substring(start, length));
    }

    public string Raw(int start, int length)
    {
        var span = Span(start, length, ReleaseSpanRole.Unknown);
        return Original.Substring(span.Start, span.Length);
    }

    internal static ReleaseTextView Create(string original, bool presentation, bool scene, ReleaseAssessmentBudget budget)
    {
        if (original.Length > 16384) throw new ArgumentException("Identity text exceeds assessment budget");
        var text = new StringBuilder(original.Length);
        var starts = new List<int>(original.Length);
        var ends = new List<int>(original.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(original);
        var pendingStart = -1;
        var pendingEnd = 0;
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var start = enumerator.ElementIndex;
            if (!budget.Spend(element.Length)) break;
            var value = string.IsNullOrWhiteSpace(element) ? " " : IdentityText.Canonical(element);
            if (presentation)
                value = value.Replace("'", "", StringComparison.Ordinal).Replace("-", " ", StringComparison.Ordinal);
            if (scene && value == "." && start > 0 && start + 1 < original.Length
                && char.IsLetterOrDigit(original[start - 1]) && char.IsLetterOrDigit(original[start + 1])
                && !(char.IsDigit(original[start - 1]) && char.IsDigit(original[start + 1])))
                value = " ";
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (pendingStart < 0) pendingStart = start;
                    pendingEnd = start + element.Length;
                    continue;
                }
                if (pendingStart >= 0 && text.Length > 0)
                {
                    text.Append(' ');
                    starts.Add(pendingStart);
                    ends.Add(pendingEnd);
                }
                pendingStart = -1;
                text.Append(c);
                starts.Add(start);
                ends.Add(start + element.Length);
            }
        }
        return new(original, text.ToString(), starts.ToArray(), ends.ToArray(), scene);
    }

    internal static bool LooksLikeScene(string release)
    {
        var separators = 0;
        for (var i = 1; i + 1 < release.Length; i++)
            if (release[i] == '.' && char.IsLetterOrDigit(release[i - 1]) && char.IsLetterOrDigit(release[i + 1])
                && !(char.IsDigit(release[i - 1]) && char.IsDigit(release[i + 1])) && ++separators >= 2)
                return true;
        return false;
    }

    internal static string SceneWords(string value)
    {
        var text = IdentityText.Presentation(value);
        var output = new StringBuilder(text);
        for (var i = 1; i + 1 < text.Length; i++)
            if (text[i] == '.' && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1])
                && !(char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))) output[i] = ' ';
        return IdentityText.Canonical(output.ToString());
    }
}
