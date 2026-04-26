using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TopHat.Transforms.JsonContext.Summarization;

/// <summary>
/// Emits per-numeric-field statistics (count / min / max / mean / p50 / p90 / p99) for dropped items.
/// Designed to let the LLM answer threshold and aggregation questions ("how many exceed 500ms?")
/// without having to re-read the originals.
/// </summary>
/// <remarks>
/// This summarizer is a TopHat addition beyond headroom's categorical-only approach. Fields without
/// enough numeric samples (or on the deny list) are skipped. Fields are ranked by sample size,
/// capped at <see cref="NumericFieldSummarizerOptions.MaxFields"/>. Returns null when no eligible
/// numeric field has enough samples.
/// </remarks>
public sealed class NumericFieldSummarizer : IDroppedItemsSummarizer
{
	private readonly NumericFieldSummarizerOptions _options;
	private readonly HashSet<string> _denySet;
	private readonly HashSet<string>? _allowSet;

	public NumericFieldSummarizer(IOptions<NumericFieldSummarizerOptions> options)
	{
		this._options = options?.Value ?? throw new ArgumentNullException(nameof(options));
		this._denySet = new HashSet<string>(this._options.FieldDenyList ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		this._allowSet = this._options.FieldAllowList is null
			? null
			: new HashSet<string>(this._options.FieldAllowList, StringComparer.OrdinalIgnoreCase);
	}

	/// <inheritdoc/>
	public string? Summarize(IReadOnlyList<JsonObject> dropped, SummarizationContext context)
	{
		ArgumentNullException.ThrowIfNull(dropped);
		ArgumentNullException.ThrowIfNull(context);

		if (dropped.Count == 0)
		{
			return null;
		}

		var samples = this.CollectSamples(dropped);

		if (samples.Count == 0)
		{
			return null;
		}

		var selected = samples
			.Where(kvp => kvp.Value.Count >= this._options.MinSampleSize)
			.OrderByDescending(kvp => kvp.Value.Count)
			.Take(this._options.MaxFields)
			.ToList();

		if (selected.Count == 0)
		{
			return null;
		}

		var parts = new List<string>(selected.Count);

		foreach (var (field, values) in selected)
		{
			parts.Add(this.FormatField(field, values));
		}

		return string.Join("; ", parts);
	}

	private Dictionary<string, List<double>> CollectSamples(IReadOnlyList<JsonObject> dropped)
	{
		var samples = new Dictionary<string, List<double>>(StringComparer.Ordinal);

		foreach (var item in dropped)
		{
			foreach (var kvp in item)
			{
				var field = kvp.Key;

				if (this._denySet.Contains(field))
				{
					continue;
				}

				if (this._allowSet is not null && !this._allowSet.Contains(field))
				{
					continue;
				}

				if (kvp.Value is not JsonValue jv)
				{
					continue;
				}

				if (!TryGetNumeric(jv, out var numeric))
				{
					continue;
				}

				if (!samples.TryGetValue(field, out var list))
				{
					list = new List<double>();
					samples[field] = list;
				}

				list.Add(numeric);
			}
		}

		return samples;
	}

	private string FormatField(string field, List<double> values)
	{
		values.Sort();

		var n = values.Count;
		var min = values[0];
		var max = values[n - 1];
		var mean = values.Sum() / n;

		var sb = new StringBuilder();
		sb.Append(field).Append(": n=").Append(n.ToString(CultureInfo.InvariantCulture));
		sb.Append(" min=").Append(FormatNumber(min));
		sb.Append(" max=").Append(FormatNumber(max));
		sb.Append(" mean=").Append(FormatNumber(mean));

		if (this._options.IncludePercentiles)
		{
			sb.Append(" p50=").Append(FormatNumber(Percentile(values, 0.50)));
			sb.Append(" p90=").Append(FormatNumber(Percentile(values, 0.90)));
			sb.Append(" p99=").Append(FormatNumber(Percentile(values, 0.99)));
		}

		return sb.ToString();
	}

	private static bool TryGetNumeric(JsonValue jv, out double value)
	{
		if (jv.TryGetValue<long>(out var l))
		{
			value = l;
			return true;
		}

		if (jv.TryGetValue<double>(out var d))
		{
			value = d;
			return !double.IsNaN(d) && !double.IsInfinity(d);
		}

		if (jv.TryGetValue<decimal>(out var m))
		{
			value = (double)m;
			return true;
		}

		if (jv.TryGetValue<int>(out var i))
		{
			value = i;
			return true;
		}

		value = 0;
		return false;
	}

	private static double Percentile(List<double> sortedValues, double fraction)
	{
		// Linear interpolation between closest ranks (matches numpy's default "linear" method).
		if (sortedValues.Count == 1)
		{
			return sortedValues[0];
		}

		var rank = fraction * (sortedValues.Count - 1);
		var lowerIdx = (int)Math.Floor(rank);
		var upperIdx = (int)Math.Ceiling(rank);

		if (lowerIdx == upperIdx)
		{
			return sortedValues[lowerIdx];
		}

		var weight = rank - lowerIdx;
		return (sortedValues[lowerIdx] * (1 - weight)) + (sortedValues[upperIdx] * weight);
	}

	private static string FormatNumber(double value)
	{
		// Integer-valued doubles render without a decimal for terseness.
		if (Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < 1e15)
		{
			return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
		}

		return value.ToString("G6", CultureInfo.InvariantCulture);
	}
}
