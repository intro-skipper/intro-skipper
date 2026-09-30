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

internal sealed record Manifest(IReadOnlyList<CorpusFile> Files);

/// <summary>
/// One boundary of a labeled credits part: an exact time, or a band where any point is right,
/// such as a fade or a cut to black with dialogue still playing. Written in JSON as a number or
/// as <c>[earliest, latest]</c>.
/// </summary>
[JsonConverter(typeof(BoundaryConverter))]
internal readonly record struct Boundary(double Earliest, double Latest)
{
    /// <summary>
    /// The signed distance from the band to a predicted time: negative before it, positive
    /// after it, zero inside it.
    /// </summary>
    public double ErrorOf(double time) => time < Earliest ? time - Earliest : time > Latest ? time - Latest : 0;
}

/// <summary>
/// One in-scope credits part: roll credits or card credits.
/// </summary>
internal sealed record LabeledPart(Boundary Start, Boundary End)
{
    /// <summary>
    /// Gets the widest span the part can cover. Time inside it is never story.
    /// </summary>
    [JsonIgnore]
    public Interval Outer => new(Start.Earliest, End.Latest);

    /// <summary>
    /// Gets the span the part always covers, empty when its bands overlap. Time inside it is always credits.
    /// </summary>
    [JsonIgnore]
    public Interval Inner => new(Start.Latest, End.Earliest);
}

/// <summary>
/// A span of seconds in file time. A span whose end is not after its start is empty.
/// </summary>
internal readonly record struct Interval(double Start, double End)
{
    [JsonIgnore]
    public double Length => Math.Max(0, End - Start);
}

/// <summary>
/// The ground truth for one corpus file. A file without credits has no parts.
/// </summary>
/// <param name="Id">The id shared with the manifest.</param>
/// <param name="Parts">The in-scope credits parts, in order.</param>
/// <param name="Holdout">Whether the file is held out: no prototype tunes on it, and the scorer reports it apart.</param>
/// <param name="Ignore">Out-of-scope credits, such as styled credits or credits over footage: never required, never penalized.</param>
internal sealed record Label(string Id, IReadOnlyList<LabeledPart> Parts, bool Holdout = false, IReadOnlyList<Interval>? Ignore = null);

internal sealed record LabelSet(IReadOnlyList<Label> Files);

/// <summary>
/// Reads and writes the runner's JSON files with one set of options. A missing required member
/// or a null where the type allows none fails the read.
/// </summary>
internal static class Json
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new InvalidDataException($"{path} is empty");
    }

    public static void Write<T>(string path, T value)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, value, Options);
    }
}

/// <summary>
/// Reads a boundary from a number or from <c>[earliest, latest]</c>. Labels are written by hand,
/// so the runner never writes one.
/// </summary>
internal sealed class BoundaryConverter : JsonConverter<Boundary>
{
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

    public override void Write(Utf8JsonWriter writer, Boundary value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}
