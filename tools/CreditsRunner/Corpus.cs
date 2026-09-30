// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreditsRunner;

/// <summary>
/// One media file of the corpus. The manifest that lists these holds local paths, so it stays
/// private; the labels, keyed by the same id, are what gets committed.
/// </summary>
/// <param name="Id">Show and episode name, the key shared with the labels.</param>
/// <param name="Path">The file, absolute or relative to the manifest.</param>
/// <param name="Duration">Jellyfin's Duration in seconds, not ffprobe's: the credits window and its cache keys derive from it.</param>
/// <param name="Movie">Whether the file is a movie, which widens the credits window.</param>
internal sealed record CorpusFile(string Id, string Path, double Duration, bool Movie = false);

/// <summary>
/// The private list of corpus files.
/// </summary>
/// <param name="Files">The files, run in this order.</param>
internal sealed record Manifest(IReadOnlyList<CorpusFile> Files);

/// <summary>
/// One boundary of a labeled credits part: an exact time, or a band where any point is right,
/// such as a fade or a cut to black with dialogue still playing. Written in JSON as a number or
/// as <c>[earliest, latest]</c>.
/// </summary>
/// <param name="Earliest">The earliest right time in seconds.</param>
/// <param name="Latest">The latest right time in seconds; equal to <paramref name="Earliest"/> for an exact time.</param>
[JsonConverter(typeof(BoundaryConverter))]
internal readonly record struct Boundary(double Earliest, double Latest)
{
    /// <summary>
    /// The signed distance from the band to a predicted time: negative before it, positive
    /// after it, zero inside it.
    /// </summary>
    /// <param name="time">The predicted time in seconds.</param>
    /// <returns>The error in seconds.</returns>
    public double ErrorOf(double time) => time < Earliest ? time - Earliest : time > Latest ? time - Latest : 0;
}

/// <summary>
/// One in-scope credits part: roll credits or card credits.
/// </summary>
/// <param name="Start">Where the part starts.</param>
/// <param name="End">Where the part ends.</param>
/// <param name="Kind">The kind, <c>roll</c> or <c>card</c>, for reading the results; the scorer ignores it.</param>
internal sealed record LabeledPart(Boundary Start, Boundary End, string? Kind = null)
{
    /// <summary>
    /// Gets the widest span the part can cover: time here is never story.
    /// </summary>
    [JsonIgnore]
    public Interval Outer => new(Start.Earliest, End.Latest);

    /// <summary>
    /// Gets the span the part always covers, empty when its bands overlap: time here is always credits.
    /// </summary>
    [JsonIgnore]
    public Interval Inner => new(Start.Latest, End.Earliest);
}

/// <summary>
/// A span of seconds in file time.
/// </summary>
/// <param name="Start">The start.</param>
/// <param name="End">The end; a span whose end is not after its start is empty.</param>
internal readonly record struct Interval(double Start, double End)
{
    /// <summary>
    /// Gets the length, zero for an empty span.
    /// </summary>
    [JsonIgnore]
    public double Length => Math.Max(0, End - Start);
}

/// <summary>
/// A named range worth watching, such as a dim scene before the roll. The scorer does not read it.
/// </summary>
/// <param name="Start">The start.</param>
/// <param name="End">The end.</param>
/// <param name="Note">What the range is.</param>
internal sealed record Trap(double Start, double End, string Note);

/// <summary>
/// The ground truth for one corpus file.
/// </summary>
/// <param name="Id">The id shared with the manifest.</param>
/// <param name="Holdout">Whether the file is held out: no prototype tunes on it, and the scorer reports it apart.</param>
/// <param name="Parts">The in-scope credits parts, in order.</param>
/// <param name="Ignore">Out-of-scope credits, such as styled credits or credits over footage: never required, never penalized.</param>
/// <param name="Traps">Named ranges worth watching.</param>
internal sealed record Label(
    string Id,
    bool Holdout,
    IReadOnlyList<LabeledPart> Parts,
    IReadOnlyList<Interval>? Ignore = null,
    IReadOnlyList<Trap>? Traps = null);

/// <summary>
/// The committed labels file.
/// </summary>
/// <param name="Files">One label per corpus file.</param>
internal sealed record LabelSet(IReadOnlyList<Label> Files);

/// <summary>
/// Reads and writes the runner's JSON files with one set of options.
/// </summary>
internal static class Json
{
    /// <summary>
    /// Gets the options every file uses: camel case, indented, comments and trailing commas allowed.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Reads a file.
    /// </summary>
    /// <typeparam name="T">The file's shape.</typeparam>
    /// <param name="path">The file.</param>
    /// <returns>The content.</returns>
    /// <exception cref="InvalidDataException">The file is JSON <c>null</c>.</exception>
    public static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new InvalidDataException($"{path} is empty");
    }

    /// <summary>
    /// Writes a file, replacing any file at the path.
    /// </summary>
    /// <typeparam name="T">The content's shape.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="value">The content.</param>
    public static void Write<T>(string path, T value)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, value, Options);
    }
}

/// <summary>
/// Reads a boundary from a number or from <c>[earliest, latest]</c>, and writes it back the same way.
/// </summary>
internal sealed class BoundaryConverter : JsonConverter<Boundary>
{
    /// <inheritdoc/>
    public override Boundary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var time = reader.GetDouble();
            return new Boundary(time, time);
        }

        var band = JsonSerializer.Deserialize<double[]>(ref reader, options);
        return band is [var earliest, var latest] && earliest <= latest
            ? new Boundary(earliest, latest)
            : throw new JsonException("A boundary is a number or [earliest, latest] with earliest <= latest.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Boundary value, JsonSerializerOptions options)
    {
        if (value.Earliest == value.Latest)
        {
            writer.WriteNumberValue(value.Earliest);
            return;
        }

        writer.WriteStartArray();
        writer.WriteNumberValue(value.Earliest);
        writer.WriteNumberValue(value.Latest);
        writer.WriteEndArray();
    }
}
