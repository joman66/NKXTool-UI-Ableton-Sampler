using System.Globalization;
using System.Text.RegularExpressions;
using NkiConverter.Audio;
using NkiConverter.Model;

namespace NkiConverter.Conversion;

/// <summary>How note names in file names are numbered.</summary>
public enum MiddleC
{
    /// <summary>MIDI note 60 is called C3 (Kontakt, Ableton, Yamaha).</summary>
    C3,

    /// <summary>MIDI note 60 is called C4 (scientific pitch notation, Logic, many sample libraries).</summary>
    C4
}

/// <summary>
/// Builds an instrument from a folder of samples which has no mapping file, e.g. the samples
/// extracted from a Kontakt NKS / NKX sample container. The mapping is derived from:
/// <list type="bullet">
/// <item>root keys and loops stored in the files (WAV 'smpl' chunk, AIFF 'INST' chunk),</item>
/// <item>note names ("C#3", "Eb2", "Cs3") or MIDI note numbers in the file names,</item>
/// <item>velocity layers ("v100", "vel3", "pp".."fff", "soft"/"hard", "layer2"),</item>
/// <item>round robins ("rr2", "seq3", "var1") and release samples ("rel", "release").</item>
/// </list>
/// Samples without any pitch information are laid out chromatically from C1 (a typical drum kit layout).
/// </summary>
public static partial class SampleFolderMapper
{
    private const int FirstChromaticKey = 36;

    private static readonly string[] GenericFolderNames = ["samples", "sample", "wav", "wavs", "audio", "ncw", "data", "files", "aiff", "aif"];

    private static readonly Dictionary<string, int> Dynamics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pppp"] = 0, ["ppp"] = 1, ["pp"] = 2, ["p"] = 3, ["mp"] = 4, ["mf"] = 5, ["f"] = 6, ["ff"] = 7, ["fff"] = 8, ["ffff"] = 9,
        ["soft"] = 2, ["low"] = 2, ["quiet"] = 2, ["light"] = 2,
        ["med"] = 5, ["medium"] = 5, ["mid"] = 5, ["normal"] = 5,
        ["hard"] = 8, ["high"] = 8, ["loud"] = 8, ["strong"] = 8, ["heavy"] = 8
    };

    private static readonly HashSet<string> ReleaseWords = new(StringComparer.OrdinalIgnoreCase) { "rel", "release", "rls", "noteoff", "releases" };

    /// <summary>Lists the folders (including the given one) which directly contain audio files.</summary>
    public static List<string> FindSampleFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(root, "*", options)
            .Where(IsAudioFile)
            .Select(f => Path.GetDirectoryName(f)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsAudioFile(string file) =>
        AudioFileInfo.SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an instrument from the audio files directly inside the folder.</summary>
    public static Instrument Map(string folder, MiddleC middleC = MiddleC.C3)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(IsAudioFile)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var instrument = new Instrument
        {
            Name = InstrumentName(folder),
            SourceFile = folder,
            SourceFormat = "sample folder, mapped from file names"
        };

        if (files.Count == 0)
        {
            return instrument;
        }

        var samples = files.Select(f => Analyze(f, middleC)).ToList();
        ResolveNumericNotes(samples);
        AssignVelocityRanges(samples, instrument);

        var pitched = samples.Where(s => s.Root >= 0).ToList();
        var unpitched = samples.Where(s => s.Root < 0).ToList();
        if (pitched.Count == 0)
        {
            if (samples.Count == 1)
            {
                samples[0].Root = 60;
                samples[0].FullRange = true;
            }
            else
            {
                // No pitch information at all: one key per sample (drum kit / one-shot layout)
                var key = FirstChromaticKey;
                foreach (var sample in samples.OrderBy(s => s.Stem, StringComparer.OrdinalIgnoreCase))
                {
                    sample.Root = Math.Min(127, key++);
                    sample.SingleKey = true;
                }

                instrument.Warnings.Add($"No note names found in the file names - the {samples.Count} samples were laid out chromatically from C1 (one key each).");
            }
        }
        else if (unpitched.Count > 0)
        {
            foreach (var sample in unpitched)
            {
                instrument.Warnings.Add($"No root key found for '{Path.GetFileName(sample.File)}' - skipped.");
            }

            samples = pitched;
        }

        // Group: release samples, round robin index
        var groups = samples
            .GroupBy(s => (s.IsRelease, s.RoundRobin))
            .OrderBy(g => g.Key.IsRelease)
            .ThenBy(g => g.Key.RoundRobin);

        var hasRoundRobin = samples.Select(s => s.RoundRobin).Distinct().Count() > 1;
        foreach (var groupSamples in groups)
        {
            var group = new SampleGroup
            {
                Name = (groupSamples.Key.IsRelease ? "Release" : "Sustain") + (hasRoundRobin ? $" RR{groupSamples.Key.RoundRobin}" : string.Empty),
                IsReleaseTrigger = groupSamples.Key.IsRelease,
                PlayLogic = hasRoundRobin ? PlayLogic.RoundRobin : PlayLogic.Always,
                RoundRobinPosition = hasRoundRobin ? groupSamples.Key.RoundRobin : 0
            };

            // Key ranges are calculated per velocity layer: each sample reaches half way to its neighbours
            foreach (var layer in groupSamples.GroupBy(s => (s.VelocityLow, s.VelocityHigh)))
            {
                var byRoot = layer.GroupBy(s => s.Root).OrderBy(g => g.Key).ToList();
                for (var i = 0; i < byRoot.Count; i++)
                {
                    var root = byRoot[i].Key;
                    var low = i == 0 ? 0 : (byRoot[i - 1].Key + root) / 2 + 1;
                    var high = i == byRoot.Count - 1 ? 127 : (root + byRoot[i + 1].Key) / 2;

                    foreach (var sample in byRoot[i])
                    {
                        var zone = new SampleZone
                        {
                            SamplePath = sample.File,
                            KeyRoot = sample.Root,
                            KeyLow = sample.SingleKey ? sample.Root : sample.FullRange ? 0 : low,
                            KeyHigh = sample.SingleKey ? sample.Root : sample.FullRange ? 127 : high,
                            VelocityLow = layer.Key.VelocityLow,
                            VelocityHigh = layer.Key.VelocityHigh,
                            Tuning = -sample.Metadata.RootTuningCents / 100.0,
                            KeyTracking = !sample.SingleKey
                        };

                        foreach (var (start, end, alternating) in sample.Metadata.Loops.Take(1))
                        {
                            zone.Loops.Add(new SampleLoop { Start = start, End = end, Type = alternating ? LoopType.Alternating : LoopType.Forward, UntilRelease = true });
                        }

                        group.Zones.Add(zone);
                    }
                }
            }

            instrument.Groups.Add(group);
        }

        instrument.VelocityToVolume = 1;
        instrument.AmpEnvelope = new Envelope { Sustain = 1, Release = 0.1 };
        instrument.Metadata.Description = $"Mapped automatically from {samples.Count} sample file(s) in '{folder}'.";
        return instrument;
    }

    private static string InstrumentName(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        while (GenericFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase) && parent is not null)
        {
            name = Path.GetFileName(parent);
            parent = Path.GetDirectoryName(parent);
        }

        return string.IsNullOrWhiteSpace(name) ? "Samples" : name;
    }

    // ---------------------------------------------------------------------------------------------

    internal sealed class SampleInfo
    {
        public required string File { get; init; }
        public required string Stem { get; init; }
        public required string[] Tokens { get; init; }
        public SampleMetadata Metadata { get; set; } = new();
        public int Root { get; set; } = -1;
        public bool SingleKey { get; set; }
        public bool FullRange { get; set; }
        public bool IsRelease { get; set; }
        public int RoundRobin { get; set; } = 1;
        public int? VelocityValue { get; set; }
        public int? VelocityIndex { get; set; }
        public int? DynamicRank { get; set; }
        public int VelocityLow { get; set; } = 1;
        public int VelocityHigh { get; set; } = 127;
        public HashSet<int> UsedTokens { get; } = [];
    }

    internal static SampleInfo Analyze(string file, MiddleC middleC)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var info = new SampleInfo
        {
            File = file,
            Stem = stem,
            Tokens = Separators().Split(stem).Where(t => t.Length > 0).ToArray(),
            Metadata = SampleMetadata.Read(file)
        };

        for (var i = 0; i < info.Tokens.Length; i++)
        {
            var token = info.Tokens[i];
            if (ReleaseWords.Contains(token))
            {
                info.IsRelease = true;
                info.UsedTokens.Add(i);
                continue;
            }

            var rr = RoundRobinToken().Match(token);
            if (rr.Success)
            {
                info.RoundRobin = Math.Max(1, int.Parse(rr.Groups[2].Value, CultureInfo.InvariantCulture));
                info.UsedTokens.Add(i);
                continue;
            }

            var velocity = VelocityToken().Match(token);
            if (velocity.Success)
            {
                info.VelocityValue = int.Parse(velocity.Groups[2].Value, CultureInfo.InvariantCulture);
                info.UsedTokens.Add(i);
                continue;
            }

            var layer = LayerToken().Match(token);
            if (layer.Success)
            {
                info.VelocityIndex = int.Parse(layer.Groups[2].Value, CultureInfo.InvariantCulture);
                info.UsedTokens.Add(i);
                continue;
            }

            if (Dynamics.TryGetValue(token, out var rank))
            {
                info.DynamicRank = rank;
                info.UsedTokens.Add(i);
            }
        }

        // Root key: stored in the file > note name > explicit "note60" token (numeric tokens are resolved per folder)
        if (info.Metadata.RootKey >= 0)
        {
            info.Root = info.Metadata.RootKey;
        }
        else
        {
            var notes = NoteName().Matches(stem);
            for (var m = notes.Count - 1; m >= 0 && info.Root < 0; m--)
            {
                var note = ParseNote(notes[m], middleC);
                if (note >= 0)
                {
                    info.Root = note;
                }
            }

            if (info.Root < 0)
            {
                for (var i = 0; i < info.Tokens.Length && info.Root < 0; i++)
                {
                    var explicitNote = NoteNumberToken().Match(info.Tokens[i]);
                    if (explicitNote.Success && int.TryParse(explicitNote.Groups[2].Value, out var n) && n is >= 0 and <= 127)
                    {
                        info.Root = n;
                        info.UsedTokens.Add(i);
                    }
                }
            }
        }

        return info;
    }

    private static int ParseNote(Match match, MiddleC middleC)
    {
        var letter = char.ToUpperInvariant(match.Groups[1].Value[0]);
        var semitone = letter switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => 0 };
        var accidental = match.Groups[2].Value;
        if (accidental is "#" or "♯" or "s" or "S")
        {
            semitone++;
        }
        else if (accidental is "b" or "♭")
        {
            semitone--;
        }

        var octave = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var note = (octave + (middleC == MiddleC.C3 ? 2 : 1)) * 12 + semitone;
        return note is >= 0 and <= 127 ? note : -1;
    }

    /// <summary>
    /// Files like "Piano_060_v3.wav" carry the MIDI note as a plain number. If no other pitch information
    /// was found, use the numeric token position whose values change the most across the folder.
    /// </summary>
    private static void ResolveNumericNotes(List<SampleInfo> samples)
    {
        var missing = samples.Where(s => s.Root < 0).ToList();
        if (missing.Count < 2)
        {
            return;
        }

        // Compare tokens counted from the end of the name (prefixes often vary in length)
        var bestPosition = -1;
        var bestDistinct = 1;
        var maxTokens = missing.Max(s => s.Tokens.Length);
        for (var fromEnd = 1; fromEnd <= maxTokens; fromEnd++)
        {
            var values = new List<int>();
            var valid = true;
            foreach (var sample in missing)
            {
                var index = sample.Tokens.Length - fromEnd;
                if (index < 0 || sample.UsedTokens.Contains(index) || !int.TryParse(sample.Tokens[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 127 || sample.Tokens[index].Length > 3)
                {
                    valid = false;
                    break;
                }

                values.Add(value);
            }

            var distinct = values.Distinct().Count();
            if (valid && distinct > bestDistinct)
            {
                bestDistinct = distinct;
                bestPosition = fromEnd;
            }
        }

        if (bestPosition < 0)
        {
            return;
        }

        foreach (var sample in missing)
        {
            var index = sample.Tokens.Length - bestPosition;
            sample.Root = int.Parse(sample.Tokens[index], CultureInfo.InvariantCulture);
            sample.UsedTokens.Add(index);
        }
    }

    private static void AssignVelocityRanges(List<SampleInfo> samples, Instrument instrument)
    {
        // 1. Real velocity values ("v40", "v80", "v127") are the upper bounds of their layers.
        // 2. Layer indexes ("v1".."v4", "layer2") and dynamics ("pp".."ff") split 1..127 evenly.
        var values = samples.Where(s => s.VelocityValue.HasValue).Select(s => s.VelocityValue!.Value).Distinct().Order().ToList();
        var looksLikeIndexes = values.Count > 0 && values.Max() <= Math.Max(16, values.Count * 2) && values.Max() < 30;

        if (values.Count > 1 && !looksLikeIndexes)
        {
            foreach (var sample in samples.Where(s => s.VelocityValue.HasValue))
            {
                var position = values.IndexOf(sample.VelocityValue!.Value);
                sample.VelocityLow = position == 0 ? 1 : Math.Clamp(values[position - 1] + 1, 1, 127);
                sample.VelocityHigh = position == values.Count - 1 ? 127 : Math.Clamp(values[position], 1, 127);
            }

            return;
        }

        if (looksLikeIndexes)
        {
            foreach (var sample in samples.Where(s => s.VelocityValue.HasValue))
            {
                sample.VelocityIndex = sample.VelocityValue;
            }
        }

        var ranks = samples
            .Select(s => s.VelocityIndex ?? s.DynamicRank)
            .Where(r => r.HasValue)
            .Select(r => r!.Value)
            .Distinct()
            .Order()
            .ToList();

        if (ranks.Count < 2)
        {
            return;
        }

        foreach (var sample in samples)
        {
            var rank = sample.VelocityIndex ?? sample.DynamicRank;
            if (!rank.HasValue)
            {
                continue;
            }

            var position = ranks.IndexOf(rank.Value);
            sample.VelocityLow = 1 + position * 127 / ranks.Count;
            sample.VelocityHigh = (position + 1) * 127 / ranks.Count;
        }

        if (samples.Any(s => s.VelocityIndex is null && s.DynamicRank is null))
        {
            instrument.Warnings.Add("Some samples have no velocity layer in their name while others do - they cover the whole velocity range.");
        }
    }

    [GeneratedRegex(@"[\s_\-.,()\[\]]+")]
    private static partial Regex Separators();

    // Note name: letter, optional accidental, octave (-2..9); not part of a longer word or number
    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Ga-g])(#|♯|b|♭|s|S)?(-?[0-9])(?![0-9])")]
    private static partial Regex NoteName();

    [GeneratedRegex(@"^(rr|r|rnd|seq|var|alt|take|round)(\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex RoundRobinToken();

    [GeneratedRegex(@"^(v|vel|velo|velocity)(\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex VelocityToken();

    [GeneratedRegex(@"^(l|layer|lyr|dyn|vl)(\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex LayerToken();

    [GeneratedRegex(@"^(n|note|key|m|midi)(\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex NoteNumberToken();
}
