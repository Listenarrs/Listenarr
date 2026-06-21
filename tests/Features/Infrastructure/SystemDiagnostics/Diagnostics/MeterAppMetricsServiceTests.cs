/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Diagnostics.Metrics;

namespace Listenarr.Tests.Features.Infrastructure.SystemDiagnostics.Diagnostics;

public sealed class MeterAppMetricsServiceTests
{
    [Fact]
    public void Metrics_AreObservableThroughStandardMeterListener()
    {
        var measurements = new List<(string Name, double Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == MeterAppMetricsService.MeterName)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, _, _) => measurements.Add((instrument.Name, measurement)));
        listener.Start();
        using var metrics = new MeterAppMetricsService();

        metrics.Increment("test.counter", 2);
        metrics.Timing("test.duration", TimeSpan.FromMilliseconds(25));
        metrics.Gauge("test.gauge", 7);
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, item => item == ("test.counter", 2));
        Assert.Contains(measurements, item => item == ("test.duration", 25));
        Assert.Contains(measurements, item => item == ("test.gauge", 7));
    }
}
