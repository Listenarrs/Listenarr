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

using Listenarr.Api.Startup;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Infrastructure.FileSystem;
using Listenarr.Infrastructure.Realtime.DependencyInjection;

var realtimeLogSink = RealtimeLoggingExtensions.CreateListenarrRealtimeLogSink();
var bootstrapFileSystem = new LocalFileSystem();
var builder = ListenarrBuilderFactory.Create(args, realtimeLogSink, bootstrapFileSystem);

builder.AddListenarrApiServices(bootstrapFileSystem);
builder.Services.AddListenarrInfrastructureComposition(builder.Configuration, builder.Environment);

var app = builder.Build();

app.Services.ApplyListenarrDatabaseMigrations();
await app.RunListenarrStartupTasksAsync();

realtimeLogSink.InitializeListenarrRealtimeLogging(app.Services);

app.UseListenarrRequestPipeline(endpoints => endpoints.MapListenarrRealtimeHubs(app.Environment));

app.Run();
