/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Audiobooks.Rules
{
    [Trait("Name", "MultiFileImportPlannerTests")]
    [Trait("Category", "Domain")]
    public class MultiFileImportPlannerTests : BaseTests
    {
        [Fact]
        public void BuildPlans_DedupesCaseOnlyPathsUsingProvidedFilesystemRules()
        {
            var root = Path.Join(Path.GetTempPath(), "listenarr-planner-" + Guid.NewGuid().ToString("N"));
            var upperPath = Path.Join(root, "Chapter01.m4b");
            var lowerPath = Path.Join(root, "chapter01.m4b");

            var plans = MultiFileImportPlanner.BuildPlans([
                (upperPath, (string?)null),
                (lowerPath, (string?)null)
            ], StringComparer.OrdinalIgnoreCase);

            Assert.Single(plans);
        }

        [Fact]
        public void BuildStableNamingNumbers_UsesProvidedFilesystemIdentityForPathKeys()
        {
            var root = Path.Join(Path.GetTempPath(), "listenarr-planner-" + Guid.NewGuid().ToString("N"));
            var upperPath = Path.Join(root, "Chapter01.m4b");
            var lowerPath = Path.Join(root, "chapter01.m4b");
            var plans = MultiFileImportPlanner.BuildPlans([
                (upperPath, (string?)null),
                (lowerPath, (string?)null)
            ], StringComparer.OrdinalIgnoreCase);

            var numbers = MultiFileImportPlanner.BuildStableNamingNumbers(
                plans,
                plan => plan.SequenceNumber,
                StringComparer.OrdinalIgnoreCase);

            Assert.Single(numbers);
        }
    }
}
