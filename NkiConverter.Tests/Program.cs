using System.IO.Compression;
using System.Xml.Linq;
using NkiConverter.Audio;
using NkiConverter.Conversion;
using NkiConverter.IO;
using NkiConverter.Kontakt;
using NkiConverter.Model;
using NkiConverter.Tests;

// Minimal self-contained test runner: dotnet run --project NkiConverter.Tests -c Release
var failures = 0;
var passed = 0;
var testDataFolder = Path.Combine(AppContext.BaseDirectory, "TestData");
var work = Path.Combine(Path.GetTempPath(), "NkiConverterTests_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);

void Check(bool condition, string message)
{
    if (condition)
    {
        passed++;
    }
    else
    {
        failures++;
        Console.WriteLine($"  FAIL: {message}");
    }
}

void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

void Near(double expected, double actual, double tolerance, string message) =>
    Check(Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected}, got {actual}");

void Run(string name, Action test)
{
    Console.WriteLine($"- {name}");
    try
    {
        test();
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  FAIL (exception): {ex}");
    }
}

XElement LoadAdv(string file)
{
    using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
    using var reader = new StreamReader(gz);
    var text = reader.ReadToEnd();
    Check(!System.Text.RegularExpressions.Regex.IsMatch(text, "%[A-Z_0-9]+%"), "no unreplaced template placeholders");
    return XDocument.Parse(text).Root!;
}

int Val(XElement e, string path)
{
    var current = e;
    foreach (var part in path.Split('/'))
    {
        current = current.Element(part) ?? throw new InvalidOperationException($"Missing element {part} in {path}");
    }

    return int.Parse((string)current.Attribute("Value")!);
}

double DVal(XElement e, string path)
{
    var current = e;
    foreach (var part in path.Split('/'))
    {
        current = current.Element(part) ?? throw new InvalidOperationException($"Missing element {part} in {path}");
    }

    return double.Parse((string)current.Attribute("Value")!, System.Globalization.CultureInfo.InvariantCulture);
}

// ------------------------------------------------------------------------------------------------

Run("FastLZ level 1 and 2 match the reference implementation", () =>
{
    var plain = File.ReadAllBytes(Path.Combine(testDataFolder, "fastlz_plain.bin"));
    foreach (var level in new[] { 1, 2 })
    {
        var compressed = File.ReadAllBytes(Path.Combine(testDataFolder, $"fastlz_l{level}.bin"));
        var output = FastLz.Decompress(compressed, plain.Length);
        Check(output.AsSpan().SequenceEqual(plain), $"level {level} output equals the original data");
    }
});

Run("Real Kontakt 6.8 template NKI (NI container) can be read", () =>
{
    var instruments = NkiReader.Read(Path.Combine(testDataFolder, "Kontakt680Template.nki"));
    Equal(1, instruments.Count, "number of programs");
    Console.WriteLine($"    name='{instruments[0].Name}', format={instruments[0].SourceFormat}, groups={instruments[0].Groups.Count}, zones={instruments[0].AllZones.Count()}, envelope={(instruments[0].AmpEnvelope is null ? "none" : "yes")}");
    foreach (var warning in instruments[0].Warnings)
    {
        Console.WriteLine($"    warning: {warning}");
    }
});

// Shared synthetic instrument definition
var files = new[] { "../Samples/Piano C3.wav", "../Samples/Piano C4.ncw", "../Samples/Release.wav", "../Samples/Missing.wav" };
var groups = new[]
{
    new TestGroup("Sustain", Volume: 0.5f, VolumeEnvelope: true, VelocityToVolume: 0.8f, PitchBend: 1f),
    new TestGroup("Release", ReleaseTrigger: true),
    new TestGroup("Muted", Muted: true)
};
var zones = new[]
{
    new TestZone(0, 0, 0, 59, 48, LowVel: 1, HighVel: 100, FadeHighKey: 4, FadeHighVel: 10, Start: 100, EndOffset: 1000, Pan: -0.25f,
        Tune: (float)Math.Pow(2, 0.25 / 12), Frames: 44100,
        Loops: [new TestLoop(2, 20000, 10000, Alternating: true, Crossfade: 500)]),
    new TestZone(0, 1, 60, 127, 60, FadeLowKey: 3, Frames: 22050, Tune: (float)Math.Pow(2, 1.0 / 12), Loops: [new TestLoop(1, 1000, 5000)]),
    new TestZone(1, 2, 0, 127, 60),
    new TestZone(2, 2, 0, 127, 60),
    new TestZone(0, 3, 0, 127, 60)
};

void CreateSamples(string libraryFolder)
{
    var samplesFolder = Path.Combine(libraryFolder, "Samples");
    Directory.CreateDirectory(samplesFolder);
    AudioBuilder.WriteWav16(Path.Combine(samplesFolder, "Piano C3.wav"), [AudioBuilder.Sine(44100, 130.81)]);
    File.WriteAllBytes(Path.Combine(samplesFolder, "Piano C4.ncw"),
        AudioBuilder.EncodeNcw([AudioBuilder.Sine(22050, 261.63), AudioBuilder.Sine(22050, 261.63, 0.3)]));
    AudioBuilder.WriteWav16(Path.Combine(samplesFolder, "Release.wav"), [AudioBuilder.Sine(4410, 440)]);
}

void VerifyInstrument(Instrument instrument, bool isXml)
{
    Equal("Test Piano", instrument.Name, "instrument name");
    Equal(2, instrument.Groups.Count, "used groups (muted group skipped)");
    var sustain = instrument.Groups[0];
    Equal("Sustain", sustain.Name, "group name");
    Equal(3, sustain.Zones.Count, "zones in the sustain group");
    Check(instrument.Groups[1].IsReleaseTrigger, "release trigger group detected");

    var z0 = sustain.Zones[0];
    Equal("../Samples/Piano C3.wav", z0.SamplePath, "sample path");
    Equal(0, z0.KeyLow, "key low");
    Equal(59, z0.KeyHigh, "key high");
    Equal(48, z0.KeyRoot, "root key");
    Equal(100, z0.VelocityHigh, "velocity high");
    Equal(4, z0.KeyFadeHigh, "key fade high");
    Equal(10, z0.VelocityFadeHigh, "velocity fade high");
    Equal(100, z0.SampleStart, "sample start");
    Near(-6.0, z0.GainDb, 0.01, "gain (program 1 * group 0.5 * zone 1)");
    Near(-0.25, z0.Pan, 0.0001, "pan");
    Near(0.25, z0.Tuning, 0.001, "tuning");
    Equal(1, z0.Loops.Count, "loop count");
    Equal(20000, z0.Loops[0].Start, "loop start");
    Equal(30000, z0.Loops[0].End, "loop end (exclusive)");
    Check(z0.Loops[0].UntilRelease, "loop until release");
    Equal(LoopType.Alternating, z0.Loops[0].Type, "alternating loop");
    Equal(500, z0.Loops[0].CrossfadeSamples, "loop crossfade");

    if (!isXml)
    {
        Equal(1000, z0.SampleEndOffsetFromEnd, "sample end offset");
        Check(instrument.AmpEnvelope is not null, "amp envelope found");
        Near(0.012, instrument.AmpEnvelope!.Attack, 0.0001, "attack");
        Near(0.005, instrument.AmpEnvelope.Hold, 0.0001, "hold");
        Near(0.25, instrument.AmpEnvelope.Decay, 0.0001, "decay");
        Near(0.7, instrument.AmpEnvelope.Sustain, 0.0001, "sustain");
        Near(0.4, instrument.AmpEnvelope.Release, 0.0001, "release");
        Near(0.8, instrument.VelocityToVolume ?? -1, 0.0001, "velocity to volume");
        Equal(12, instrument.PitchBendRange, "pitch bend range");
        Equal("Test Author", instrument.Metadata.Author, "author");
    }
}

void VerifyAdv(string presetFile, string outputFolder)
{
    var root = LoadAdv(presetFile);
    var sampler = root.Element("MultiSampler")!;
    Equal("Test Piano", (string)sampler.Element("UserName")!.Attribute("Value")!, "preset name");
    var annotation = (string)sampler.Element("Annotation")!.Attribute("Value")!;
    Check(annotation.Contains("Converted from Kontakt instrument"), "annotation contains conversion note");
    Check(annotation.Contains("Author: Test Author") || annotation.Contains("Author: Hdr Auth"), "annotation contains author");

    var parts = root.Descendants("MultiSamplePart").ToList();
    Equal(2, parts.Count, "zones in the preset (release + missing zones skipped)");

    var p0 = parts[0];
    Equal(0, Val(p0, "KeyRange/CrossfadeMin"), "key crossfade min");
    Equal(0, Val(p0, "KeyRange/Min"), "key min");
    Equal(55, Val(p0, "KeyRange/Max"), "key max (59 - fade 4)");
    Equal(59, Val(p0, "KeyRange/CrossfadeMax"), "key crossfade max");
    Equal(90, Val(p0, "VelocityRange/Max"), "velocity max (100 - fade 10)");
    Equal(100, Val(p0, "VelocityRange/CrossfadeMax"), "velocity crossfade max");
    Equal(48, Val(p0, "RootKey"), "root key");
    Equal(25, Val(p0, "Detune"), "detune cents");
    Equal(100, Val(p0, "SampleStart"), "sample start");
    Equal(44100 - 1000, Val(p0, "SampleEnd"), "sample end");
    Equal(20000, Val(p0, "SustainLoop/Start"), "loop start");
    Equal(30000, Val(p0, "SustainLoop/End"), "loop end");
    Equal(2, Val(p0, "SustainLoop/Mode"), "loop mode alternating");
    Equal(500, Val(p0, "SustainLoop/Crossfade"), "loop crossfade");
    Equal(0, Val(p0, "ReleaseLoop/Mode"), "release mode for loop until release");
    Near(-0.25, DVal(p0, "Panorama"), 0.0001, "pan");
    Equal(44100, Val(p0, "SampleRef/DefaultDuration"), "sample duration");

    var p1 = parts[1];
    Equal(60, Val(p1, "KeyRange/CrossfadeMin"), "zone 2 key crossfade min");
    Equal(63, Val(p1, "KeyRange/Min"), "zone 2 key min (60 + fade 3)");
    Equal(59, Val(p1, "RootKey"), "zone 2 root key (60 shifted by +1 semitone tuning)");
    Equal(1, Val(p1, "SustainLoop/Mode"), "zone 2 forward loop");
    Equal(3, Val(p1, "ReleaseLoop/Mode"), "zone 2 loop until end");
    Equal(22050, Val(p1, "SampleRef/DefaultDuration"), "decoded NCW duration");

    var relativePath = (string)p1.Element("SampleRef")!.Element("FileRef")!.Element("RelativePath")!.Attribute("Value")!;
    Check(relativePath.StartsWith("Samples/Imported/Test Piano/") && relativePath.EndsWith(".wav"), $"NCW written as WAV in the sample folder ({relativePath})");
    var decodedFile = Path.Combine(outputFolder, relativePath);
    Check(File.Exists(decodedFile), "decoded sample exists");
    var info = AudioFileInfo.Read(decodedFile);
    Equal(2, info.Channels, "decoded channels");
    Equal(22050, info.Frames, "decoded frames");

    Near(12, DVal(sampler, "VolumeAndPan/Envelope/AttackTime/Manual"), 0.001, "amp attack ms");
    Near(255, DVal(sampler, "VolumeAndPan/Envelope/DecayTime/Manual"), 0.001, "amp decay ms (hold + decay)");
    Near(0.7, DVal(sampler, "VolumeAndPan/Envelope/SustainLevel/Manual"), 0.0001, "amp sustain");
    Near(400, DVal(sampler, "VolumeAndPan/Envelope/ReleaseTime/Manual"), 0.001, "amp release ms");
    Equal(12, Val(sampler, "Globals/PitchBendRange"), "pitch bend range");
}

void RunEndToEnd(string name, byte[] nkiBytes, bool isXml)
{
    var library = Path.Combine(work, name);
    var instruments = Path.Combine(library, "Instruments");
    Directory.CreateDirectory(instruments);
    CreateSamples(library);
    var nki = Path.Combine(instruments, "Test Piano.nki");
    File.WriteAllBytes(nki, nkiBytes);

    var parsed = NkiReader.Read(nki);
    Equal(1, parsed.Count, "programs");
    VerifyInstrument(parsed[0], isXml);

    var output = Path.Combine(work, name + "_out");
    var converter = new AbletonConverter(new ConversionOptions { OutputFolder = output, AbletonVersion = 12 });
    var result = converter.Convert(nki);
    Check(result.Success, $"conversion succeeded ({result.Error})");
    foreach (var warning in result.Warnings)
    {
        Console.WriteLine($"    warning: {warning}");
    }

    Check(result.Warnings.Any(w => w.Contains("not found")), "missing sample reported");
    Check(result.Warnings.Any(w => w.Contains("release-trigger")), "release trigger groups reported");
    if (result.Success)
    {
        VerifyAdv(result.PresetFiles[0], output);
    }
}

Run("Kontakt 5+ container NKI -> Ableton Sampler (end to end)", () =>
    RunEndToEnd("k5", KontaktBuilder.Kontakt5Container(KontaktBuilder.PresetData("Test Piano", groups, zones, files, author: "Test Author")), false));

Run("Kontakt 4.2 NKI -> Ableton Sampler (end to end)", () =>
    RunEndToEnd("k42", KontaktBuilder.Kontakt42File(KontaktBuilder.PresetData("Test Piano", groups, zones, files, author: "Test Author")), false));

Run("Kontakt 2-4.1 XML NKI -> Ableton Sampler (end to end)", () =>
{
    const string xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <K2_Container><Programs><K2_Program name="Test Piano" version="0.6">
          <Parameters><V name="volume" value="1"/><V name="pan" value="0"/><V name="tune" value="1"/></Parameters>
          <Groups>
            <K2_Group index="0" name="Sustain">
              <Parameters><V name="volume" value="0.5"/><V name="pan" value="0"/><V name="tune" value="1"/><V name="keyTracking" value="yes"/><V name="releaseTrigger" value="no"/></Parameters>
              <IntModulators><K2_IntMod><V name="bypass" value="no"/>
                <Envelope type="ahdsr"><V name="attack" value="12"/><V name="hold" value="5"/><V name="decay" value="250"/><V name="sustain" value="0.7"/><V name="release" value="400"/></Envelope>
                <Targets><Target><V name="target" value="volume"/><V name="intensity" value="1"/></Target></Targets>
              </K2_IntMod></IntModulators>
              <ExtModulators><K2_ExtMod><V name="source" value="pitchBend"/><Targets><Target><V name="target" value="pitch"/><V name="intensity" value="1"/></Target></Targets></K2_ExtMod></ExtModulators>
            </K2_Group>
            <K2_Group index="1" name="Release"><Parameters><V name="volume" value="1"/><V name="pan" value="0"/><V name="tune" value="1"/><V name="keyTracking" value="yes"/><V name="releaseTrigger" value="yes"/></Parameters></K2_Group>
          </Groups>
          <Zones>
            <K2_Zone groupIdx="0"><Sample><V name="file_ex2" value="@bd007Samplesf000000Piano C3.wav"/></Sample>
              <Parameters><V name="rootKey" value="48"/><V name="lowKey" value="0"/><V name="highKey" value="59"/><V name="lowVelocity" value="1"/><V name="highVelocity" value="100"/>
                <V name="fadeLowKey" value="0"/><V name="fadeHighKey" value="4"/><V name="fadeLowVelo" value="0"/><V name="fadeHighVelo" value="10"/>
                <V name="sampleStart" value="100"/><V name="sampleEnd" value="-1000"/><V name="zoneVolume" value="1"/><V name="zonePan" value="-0.25"/><V name="zoneTune" value="1.0145453349375237"/></Parameters>
              <Loops><Loop><V name="mode" value="until_release"/><V name="loopStart" value="20000"/><V name="loopLength" value="10000"/><V name="xfadeLength" value="500"/><V name="alternatingLoop" value="yes"/><V name="loopTuning" value="1"/></Loop></Loops>
            </K2_Zone>
            <K2_Zone groupIdx="0"><Sample><V name="file_ex2" value="../Samples/Piano C4.ncw"/></Sample>
              <Parameters><V name="rootKey" value="60"/><V name="lowKey" value="60"/><V name="highKey" value="127"/><V name="lowVelocity" value="1"/><V name="highVelocity" value="127"/>
                <V name="fadeLowKey" value="3"/><V name="fadeHighKey" value="0"/><V name="fadeLowVelo" value="0"/><V name="fadeHighVelo" value="0"/>
                <V name="sampleStart" value="0"/><V name="sampleEnd" value="0"/><V name="zoneVolume" value="1"/><V name="zonePan" value="0"/><V name="zoneTune" value="1.0594630943592953"/></Parameters>
              <Loops><Loop><V name="mode" value="until_end"/><V name="loopStart" value="1000"/><V name="loopLength" value="5000"/><V name="xfadeLength" value="0"/><V name="alternatingLoop" value="no"/><V name="loopTuning" value="1"/></Loop></Loops>
            </K2_Zone>
            <K2_Zone groupIdx="0"><Sample><V name="file_ex2" value="../Samples/Missing.wav"/></Sample><Parameters><V name="rootKey" value="60"/></Parameters></K2_Zone>
            <K2_Zone groupIdx="1"><Sample><V name="file_ex2" value="../Samples/Release.wav"/></Sample><Parameters><V name="rootKey" value="60"/></Parameters></K2_Zone>
          </Zones>
        </K2_Program></Programs></K2_Container>
        """;
    RunEndToEnd("k2", KontaktBuilder.Kontakt2File(xml), true);
});

Run("NCW decoder (delta, truncated, raw and mid/side blocks)", () =>
{
    var left = AudioBuilder.Sine(3000, 440, 0.8);
    var right = AudioBuilder.Sine(3000, 660, 0.6);
    var ncw = AudioBuilder.EncodeNcw([left, right]);
    var decoded = NcwDecoder.Decode(ncw);
    Equal(2, decoded.Channels, "channels");
    Equal(3000, decoded.Frames, "frames");
    Check(decoded.Samples[0].Select(s => (short)s).SequenceEqual(left), "left channel identical");
    Check(decoded.Samples[1].Select(s => (short)s).SequenceEqual(right), "right channel identical");
    var dumpTarget = Environment.GetEnvironmentVariable("NKI_TEST_NCW_OUT");
    if (!string.IsNullOrEmpty(dumpTarget))
    {
        File.WriteAllBytes(dumpTarget, ncw); // for cross-checking with other decoders
    }
});

Run("Round robin groups use native Live 12 round robin / selector ranges for Live 11", () =>
{
    var rrGroups = new[] { new TestGroup("RR1", StartMode: 3, CycleClass: 0), new TestGroup("RR2", StartMode: 3, CycleClass: 1) };
    var rrZones = new[] { new TestZone(0, 0, 0, 127, 60), new TestZone(1, 1, 0, 127, 60) };
    var rrFiles = new[] { "a.wav", "b.wav" };
    var folder = Path.Combine(work, "rr");
    Directory.CreateDirectory(folder);
    AudioBuilder.WriteWav16(Path.Combine(folder, "a.wav"), [AudioBuilder.Sine(1000, 100)]);
    AudioBuilder.WriteWav16(Path.Combine(folder, "b.wav"), [AudioBuilder.Sine(1000, 200)]);
    var nki = Path.Combine(folder, "RR.nki");
    File.WriteAllBytes(nki, KontaktBuilder.Kontakt5Container(KontaktBuilder.PresetData("RR", rrGroups, rrZones, rrFiles)));

    var live12 = new AbletonConverter(new ConversionOptions { OutputFolder = Path.Combine(work, "rr12"), AbletonVersion = 12 }).Convert(nki);
    Check(live12.Success, $"Live 12 conversion ({live12.Error})");
    var root12 = LoadAdv(live12.PresetFiles[0]);
    Equal("true", (string)root12.Descendants("RoundRobin").First().Attribute("Value")!, "native round robin enabled");

    var live11 = new AbletonConverter(new ConversionOptions { OutputFolder = Path.Combine(work, "rr11"), AbletonVersion = 11 }).Convert(nki);
    Check(live11.Success, $"Live 11 conversion ({live11.Error})");
    var parts = LoadAdv(live11.PresetFiles[0]).Descendants("MultiSamplePart").ToList();
    Equal(0, Val(parts[0], "SelectorRange/Min"), "first round robin slice min");
    Equal(63, Val(parts[0], "SelectorRange/Max"), "first round robin slice max");
    Equal(64, Val(parts[1], "SelectorRange/Min"), "second round robin slice min");
    Equal(127, Val(parts[1], "SelectorRange/Max"), "second round robin slice max");
});

Run("Samples are found by name when the folder layout differs", () =>
{
    var root = Path.Combine(work, "layout");
    var nkiFolder = Path.Combine(root, "Instruments", "Sub");
    var otherFolder = Path.Combine(root, "Other", "Deep");
    Directory.CreateDirectory(nkiFolder);
    Directory.CreateDirectory(otherFolder);
    AudioBuilder.WriteWav16(Path.Combine(otherFolder, "x.wav"), [AudioBuilder.Sine(500, 100)]);
    var resolver = new SampleResolver([root]);
    var found = resolver.Resolve(Path.Combine(nkiFolder, "i.nki"), "C:/Users/someone/Library/Samples/x.ncw");
    Equal(Path.Combine(otherFolder, "x.wav"), found, "resolved by file name with different extension");
});

Run("Encoded Kontakt 2 sample paths are decoded", () =>
{
    Equal("../Samples/Piano C3.wav", Kontakt2Reader.DecodeSamplePath("@bd007Samplesf000000Piano C3.wav"), "relative path");
    Equal("Samples/x.wav", Kontakt2Reader.DecodeSamplePath("@m007Libraryd007SamplesF00000000000x.wav"), "library path");
    Equal("/HD/a.wav", Kontakt2Reader.DecodeSamplePath("@v002HDf000000a.wav"), "absolute path");
});


// ---------------- Sample folders (NKS / NKX content without NKI) ----------------

string MakeFolder(string name, params string[] fileNames)
{
    var folder = Path.Combine(work, "folders", name);
    Directory.CreateDirectory(folder);
    foreach (var fileName in fileNames)
    {
        AudioBuilder.WriteWav16(Path.Combine(folder, fileName), [AudioBuilder.Sine(2000, 220)]);
    }

    return folder;
}

SampleZone ZoneOf(Instrument instrument, string fileName) =>
    instrument.AllZones.Single(z => Path.GetFileName(z.SamplePath) == fileName);

Run("Sample folder: note names, velocity values and release samples", () =>
{
    var folder = MakeFolder("Grand Piano", "Piano_C3_v40.wav", "Piano_C3_v127.wav", "Piano_E3_v40.wav", "Piano_E3_v127.wav", "Piano_C3_rel.wav");
    var instrument = SampleFolderMapper.Map(folder);
    Equal("Grand Piano", instrument.Name, "instrument name from folder");
    var c3 = ZoneOf(instrument, "Piano_C3_v40.wav");
    Equal(60, c3.KeyRoot, "C3 = 60");
    Equal(0, c3.KeyLow, "C3 low key");
    Equal(62, c3.KeyHigh, "C3 high key (half way to E3)");
    Equal(1, c3.VelocityLow, "v40 layer low");
    Equal(40, c3.VelocityHigh, "v40 layer high");
    var e3 = ZoneOf(instrument, "Piano_E3_v127.wav");
    Equal(64, e3.KeyRoot, "E3 = 64");
    Equal(63, e3.KeyLow, "E3 low key");
    Equal(127, e3.KeyHigh, "E3 high key");
    Equal(41, e3.VelocityLow, "v127 layer low");
    Equal(127, e3.VelocityHigh, "v127 layer high");
    Check(instrument.Groups.Any(g => g.IsReleaseTrigger && g.Zones.Count == 1), "release sample in a release group");
});

Run("Sample folder: root key and loop from the WAV 'smpl' chunk", () =>
{
    var folder = Path.Combine(work, "folders", "Pad");
    Directory.CreateDirectory(folder);
    AudioBuilder.WriteWav16(Path.Combine(folder, "pad sample 1.wav"), [AudioBuilder.Sine(4000, 220)], smplRoot: 57, loopStart: 1000, loopEnd: 2999);
    AudioBuilder.WriteWav16(Path.Combine(folder, "pad sample 2.wav"), [AudioBuilder.Sine(4000, 440)], smplRoot: 69);
    var instrument = SampleFolderMapper.Map(folder);
    var z = ZoneOf(instrument, "pad sample 1.wav");
    Equal(57, z.KeyRoot, "root from smpl");
    Equal(1, z.Loops.Count, "loop from smpl");
    Equal(1000, z.Loops[0].Start, "loop start");
    Equal(3000, z.Loops[0].End, "loop end (exclusive)");
    Equal(69, ZoneOf(instrument, "pad sample 2.wav").KeyRoot, "second root from smpl");
});

Run("Sample folder: MIDI note numbers, dynamics and round robins", () =>
{
    var numbers = SampleFolderMapper.Map(MakeFolder("Bass", "Bass_036.wav", "Bass_048.wav", "Bass_060.wav"));
    Equal(36, ZoneOf(numbers, "Bass_036.wav").KeyRoot, "MIDI number 36");
    Equal(60, ZoneOf(numbers, "Bass_060.wav").KeyRoot, "MIDI number 60");

    var dynamics = SampleFolderMapper.Map(MakeFolder("Strings", "Vln A3 pp.wav", "Vln A3 mf.wav", "Vln A3 ff.wav"));
    Equal(1, ZoneOf(dynamics, "Vln A3 pp.wav").VelocityLow, "pp low");
    Equal(42, ZoneOf(dynamics, "Vln A3 pp.wav").VelocityHigh, "pp high");
    Equal(85, ZoneOf(dynamics, "Vln A3 ff.wav").VelocityLow, "ff low");
    Equal(69, ZoneOf(dynamics, "Vln A3 ff.wav").KeyRoot, "A3 = 69 (C3 = 60)");

    var rr = SampleFolderMapper.Map(MakeFolder("Snare RR", "Snare_D1_rr1.wav", "Snare_D1_rr2.wav", "Snare_D1_rr3.wav"));
    Equal(3, rr.Groups.Count, "one group per round robin");
    Check(rr.Groups.All(g => g.PlayLogic == PlayLogic.RoundRobin), "groups cycle");
    Equal(38, rr.AllZones.First().KeyRoot, "D1 = 38");

    var sharps = SampleFolderMapper.Map(MakeFolder("Sharps", "Gtr_C#2.wav", "Gtr_Eb2.wav", "Gtr_Fs2.wav"));
    Equal(49, ZoneOf(sharps, "Gtr_C#2.wav").KeyRoot, "C#2");
    Equal(51, ZoneOf(sharps, "Gtr_Eb2.wav").KeyRoot, "Eb2");
    Equal(54, ZoneOf(sharps, "Gtr_Fs2.wav").KeyRoot, "Fs2 = F#2");

    var c4 = SampleFolderMapper.Map(MakeFolder("MiddleC4", "Keys C4.wav", "Keys G4.wav"), MiddleC.C4);
    Equal(60, ZoneOf(c4, "Keys C4.wav").KeyRoot, "C4 = 60 with the C4 convention");
});

Run("Sample folder: drum kit without note names is laid out chromatically", () =>
{
    var kit = SampleFolderMapper.Map(MakeFolder("Kit", "Kick.wav", "Snare.wav", "HiHat Closed.wav"));
    Equal(36, ZoneOf(kit, "HiHat Closed.wav").KeyRoot, "first (alphabetical) on C1");
    Equal(37, ZoneOf(kit, "Kick.wav").KeyLow, "one key per sample");
    Equal(37, ZoneOf(kit, "Kick.wav").KeyHigh, "one key per sample");
    Check(!ZoneOf(kit, "Snare.wav").KeyTracking, "drum zones are not transposed");
});

Run("Sample folder -> Ableton Sampler (end to end, Live 12)", () =>
{
    var root = Path.Combine(work, "nks_extract");
    var samples = Path.Combine(root, "Library", "Samples");
    Directory.CreateDirectory(samples);
    foreach (var name in new[] { "Rhodes_C2_v1.wav", "Rhodes_C2_v2.wav", "Rhodes_C4_v1.wav", "Rhodes_C4_v2.wav" })
    {
        AudioBuilder.WriteWav16(Path.Combine(samples, name), [AudioBuilder.Sine(3000, 110)]);
    }

    File.WriteAllBytes(Path.Combine(samples, "Rhodes_C3_v1.ncw"), AudioBuilder.EncodeNcw([AudioBuilder.Sine(3000, 220)]));

    var folders = SampleFolderMapper.FindSampleFolders(root);
    Equal(1, folders.Count, "one sample folder found");
    var output = Path.Combine(work, "nks_out");
    var result = new AbletonConverter(new ConversionOptions { OutputFolder = output }).ConvertSampleFolder(folders[0]);
    Check(result.Success, $"conversion succeeded ({result.Error})");
    Check(Path.GetFileName(result.PresetFiles[0]) == "Library.adv", $"generic 'Samples' folder named after its parent ({Path.GetFileName(result.PresetFiles[0])})");
    var parts = LoadAdv(result.PresetFiles[0]).Descendants("MultiSamplePart").ToList();
    Equal(5, parts.Count, "zones");
    var layer1 = parts.Where(p => Val(p, "VelocityRange/Max") == 63).ToList();
    Equal(3, layer1.Count, "three samples in velocity layer 1 (incl. decoded NCW)");
    Check(parts.Any(p => Val(p, "RootKey") == 60 && Val(p, "KeyRange/Min") == 55 && Val(p, "KeyRange/Max") == 66), "C3 zone spans half way to its neighbours");
});

try
{
    Directory.Delete(work, true);
}
catch (IOException)
{
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"All {passed} checks passed." : $"{failures} check(s) FAILED, {passed} passed.");
return failures == 0 ? 0 : 1;
