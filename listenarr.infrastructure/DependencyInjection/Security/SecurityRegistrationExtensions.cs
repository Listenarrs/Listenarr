/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection.Security;

internal static class SecurityRegistrationExtensions
{
    public static IServiceCollection AddConfigurationAndSecurityServices(this IServiceCollection services)
    {
        services.AddScoped<IConfigurationService, ConfigurationService>();
        services.AddDataProtection();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IRequestContextAccessor, AspNetRequestContextAccessor>();
        services.AddSingleton<IStartupConfigService, StartupConfigService>();
        services.AddScoped<IUserService, UserService>();
        services.AddSingleton<ILoginRateLimiter, LoginRateLimiter>();
        services.AddScoped<SessionService>();
        services.AddScoped<ISessionService, ConditionalSessionService>();
        return services;
    }

    public static IServiceCollection AddSecurityInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IUserSessionRepository, EfUserSessionRepository>();
        services.AddScoped<IApplicationSettingsRepository, EfApplicationSettingsRepository>();
        services.AddScoped<IApiConfigurationRepository, EfApiConfigurationRepository>();
        return services;
    }
}
