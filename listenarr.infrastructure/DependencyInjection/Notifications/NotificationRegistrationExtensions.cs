/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.DependencyInjection.Notifications;

internal static class NotificationRegistrationExtensions
{
    public static IServiceCollection AddNotificationAndRealtimeServices(this IServiceCollection services)
    {
        services.AddHttpClient<NotificationService>();
        services.AddScoped<NotificationService>(provider =>
            ActivatorUtilities.CreateInstance<NotificationService>(provider));
        services.AddScoped<INotificationService>(provider =>
            provider.GetRequiredService<NotificationService>());
        services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();
        services.AddSingleton<IDiscordBotService, DiscordBotService>();
        services.AddSingleton<IToastService, ToastService>();
        services.AddSingleton<IHubBroadcaster, SignalRHubBroadcaster>();
        services.AddSingleton<IRealtimeClientRegistry, SignalRClientRegistry>();
        services.AddHttpContextAccessor();
        return services;
    }
}
