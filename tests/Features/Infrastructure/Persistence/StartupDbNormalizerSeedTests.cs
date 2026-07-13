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

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    public sealed class StartupDbNormalizerSeedTests : BaseTests
    {
        [Fact]
        public async Task StartAsync_OnFreshDatabase_SeedsExactlyOneDefaultProfile()
        {
            // Given a fresh database with no quality profiles
            Assert.Empty(await _qualityProfileRepository.GetAllAsync());
            var normalizer = new StartupDbNormalizer(
                _provider,
                _provider.GetRequiredService<ILogger<StartupDbNormalizer>>());

            // When the startup normalizer runs
            await normalizer.StartAsync(CancellationToken.None);

            // Then exactly one default profile has been seeded
            var profile = Assert.Single(await _qualityProfileRepository.GetAllAsync());
            Assert.True(profile.IsDefault);
        }

        [Fact]
        public async Task StartAsync_RunTwice_RemainsExactlyOneProfile()
        {
            var normalizer = new StartupDbNormalizer(
                _provider,
                _provider.GetRequiredService<ILogger<StartupDbNormalizer>>());

            // When the startup normalizer runs twice
            await normalizer.StartAsync(CancellationToken.None);
            await normalizer.StartAsync(CancellationToken.None);

            // Then seeding stays idempotent
            Assert.Single(await _qualityProfileRepository.GetAllAsync());
        }
    }
}
