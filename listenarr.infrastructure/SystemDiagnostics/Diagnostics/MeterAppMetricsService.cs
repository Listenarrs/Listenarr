/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Listenarr.Infrastructure.SystemDiagnostics.Diagnostics;

public sealed class MeterAppMetricsService : IAppMetricsService, IDisposable
{
    public const string MeterName = "Listenarr.Backend";
    private readonly Meter _meter = new(MeterName);
    private readonly ConcurrentDictionary<string, Counter<double>> _counters = new();
    private readonly ConcurrentDictionary<string, Histogram<double>> _timings = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, ObservableGauge<double>> _gaugeInstruments = new();

    public void Increment(string metricName, double value = 1) =>
        _counters.GetOrAdd(metricName, name => _meter.CreateCounter<double>(name)).Add(value);

    public void Gauge(string metricName, double value)
    {
        _gauges[metricName] = value;
        _gaugeInstruments.GetOrAdd(
            metricName,
            name => _meter.CreateObservableGauge(
                name,
                () => _gauges.TryGetValue(name, out var current) ? current : 0));
    }

    public void Timing(string metricName, TimeSpan duration) =>
        _timings.GetOrAdd(
            metricName,
            name => _meter.CreateHistogram<double>(name, "ms"))
        .Record(duration.TotalMilliseconds);

    public void Dispose() => _meter.Dispose();
}
