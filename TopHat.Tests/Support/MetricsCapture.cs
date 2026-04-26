using System.Diagnostics.Metrics;
using System.Globalization;
using TopHat.Diagnostics;

namespace TopHat.Tests.Support;

/// <summary>
/// Collects all instrument recordings on the "TopHat" meter during a test. Disposable lifetime scopes the capture.
/// </summary>
internal sealed class MetricsCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Recording> _recordings = new();
    private readonly Lock _gate = new();

    public IReadOnlyList<Recording> Recordings
    {
        get
        {
            lock (this._gate)
            {
                return this._recordings.ToArray();
            }
        }
    }

    public MetricsCapture()
    {
        this._listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == TopHatMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        this._listener.SetMeasurementEventCallback<long>(this.Record);
        this._listener.SetMeasurementEventCallback<double>(this.Record);
        this._listener.SetMeasurementEventCallback<int>((inst, m, tags, _) => this.Record(inst, m, tags, null));
        this._listener.Start();
    }

    private void Record<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? _) where T : struct
    {
        var copied = new KeyValuePair<string, object?>[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            copied[i] = tags[i];
        }

        lock (this._gate)
        {
            this._recordings.Add(new Recording(instrument.Name, Convert.ToDouble(measurement, CultureInfo.InvariantCulture), copied));
        }
    }

    public IEnumerable<Recording> ForInstrument(string name) => this.Recordings.Where(r => r.InstrumentName == name);

    public void Dispose() => this._listener.Dispose();

    internal sealed record Recording(string InstrumentName, double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public object? Tag(string key)
        {
            foreach (var t in this.Tags)
            {
                if (t.Key == key)
                {
                    return t.Value;
                }
            }

            return null;
        }
    }
}
