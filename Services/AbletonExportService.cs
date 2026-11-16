using System.IO;
using NkiConverter.Conversion;

namespace NkxToolUI.Services;

/// <summary>Runs the NKI -> Ableton Sampler conversion in the background and reports progress as log lines.</summary>
public static class AbletonExportService
{
    /// <summary>Name of the folder (inside the unpack output folder) which receives the Ableton presets.</summary>
    public const string DefaultOutputFolderName = "Ableton Sampler";

    /// <summary>
    /// Finds what to convert below a folder: the .nki instruments if there are any (they contain the exact
    /// mapping), otherwise every folder with samples (e.g. the content of an NKS sample container) becomes
    /// an instrument mapped from its file names.
    /// </summary>
    public static List<string> CollectSources(string folder, string? excludeFolder = null)
    {
        bool Keep(string path) =>
            excludeFolder is null || !path.StartsWith(excludeFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        var nkiFiles = AbletonConverter.FindNkiFiles(folder).Where(Keep).ToList();
        return nkiFiles.Count > 0 ? nkiFiles : SampleFolderMapper.FindSampleFolders(folder).Where(Keep).ToList();
    }

    /// <param name="sources">.nki files and/or sample folders.</param>
    public static Task<AbletonExportSummary> ConvertAsync(
        IReadOnlyList<string> sources,
        string outputFolder,
        int abletonVersion,
        MiddleC middleC,
        IReadOnlyList<string> sampleSearchFolders,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Convert(sources, outputFolder, abletonVersion, middleC, sampleSearchFolders, log, cancellationToken), cancellationToken);
    }

    private static AbletonExportSummary Convert(
        IReadOnlyList<string> nkiFiles,
        string outputFolder,
        int abletonVersion,
        MiddleC middleC,
        IReadOnlyList<string> sampleSearchFolders,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var summary = new AbletonExportSummary { OutputFolder = outputFolder };
        Directory.CreateDirectory(outputFolder);

        var converter = new AbletonConverter(
            new ConversionOptions
            {
                OutputFolder = outputFolder,
                AbletonVersion = abletonVersion,
                SampleSearchFolders = sampleSearchFolders
            },
            log);

        log($"Converting {nkiFiles.Count} instrument(s) to Ableton Live {abletonVersion} Sampler presets.");
        log($"Output: {outputFolder}");

        for (var i = 0; i < nkiFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nki = nkiFiles[i];
            var isSampleFolder = Directory.Exists(nki);
            log(isSampleFolder
                ? $"[{i + 1}/{nkiFiles.Count}] Sample folder {nki}"
                : $"[{i + 1}/{nkiFiles.Count}] {Path.GetFileName(nki)}");

            var result = isSampleFolder
                ? converter.ConvertSampleFolder(nki, middleC, cancellationToken)
                : converter.Convert(nki, cancellationToken);
            foreach (var warning in result.Warnings)
            {
                log($"    ! {warning}");
            }

            if (result.Success)
            {
                summary.Converted++;
                summary.Presets += result.PresetFiles.Count;
                foreach (var preset in result.PresetFiles)
                {
                    log($"    -> {Path.GetRelativePath(outputFolder, preset)}");
                }
            }
            else
            {
                summary.Failed++;
                log($"    ERROR: {result.Error ?? "No preset was created."}");
            }
        }

        log(summary.Failed == 0
            ? $"Done: {summary.Presets} preset(s) created."
            : $"Done: {summary.Presets} preset(s) created, {summary.Failed} instrument(s) failed.");

        if (summary.Presets > 0)
        {
            log("Tip: copy the 'Presets' and 'Samples' folders into your Ableton User Library, or drag an .adv file onto a MIDI track in Live.");
        }

        return summary;
    }
}

public sealed class AbletonExportSummary
{
    public string OutputFolder { get; init; } = string.Empty;
    public int Converted { get; set; }
    public int Failed { get; set; }
    public int Presets { get; set; }
}
