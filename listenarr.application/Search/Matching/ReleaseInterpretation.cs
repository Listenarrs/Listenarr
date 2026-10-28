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
namespace Listenarr.Application.Search.Matching;

public enum ReleaseSpanRole
{
    PrimaryTitle, AuthorCredit, SeriesName, SeriesPosition, NarratorCredit, TechnicalMetadata, GroupMarker, Unknown, ReferenceSubtitle
}

public sealed record ReleaseSpan(int Start, int Length, ReleaseSpanRole Role, string Value)
{
    public IdentityEvidenceOrigin Origin => IdentityEvidenceOrigin.ReleaseNameDerived;
}

public sealed record ReleaseSeriesReference(string Name, string? Position);

public sealed record ReferenceTitleParts(string MainTitle, string Subtitle);

public sealed record ReleaseInterpretation(
    bool Complete, bool Credited, string? Title, bool FoundTitle,
    IReadOnlyList<ReleaseSpan> Spans, string? UnavailableReason = null);

internal sealed class ReleaseAssessmentBudget(CancellationToken cancellationToken)
{
    private int _remaining = 1_000_000;
    public bool Exhausted => _remaining < 0;
    public bool Spend(int amount = 1)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _remaining -= Math.Max(1, amount);
        return !Exhausted;
    }
}
