namespace Strayta.Segmentation;

/// <summary>
/// Where the model files live. They are too large for git, so a build step downloads them (see models/MODELS.md);
/// everything that needs them checks <see cref="HasObjectModel"/> / <see cref="HasSubjectModel"/> first and
/// explains <see cref="FetchHint"/> when they are missing.
/// </summary>
public sealed class SegmentationModels
{
    /// <summary>SAM 2.1 Hiera-S image encoder (run once per image).</summary>
    public const string SamEncoderFile = "sam2.1_hiera_small.encoder.onnx";

    /// <summary>SAM 2.1 prompt encoder + mask decoder (run per prompt).</summary>
    public const string SamDecoderFile = "sam2.1_hiera_small.decoder.onnx";

    /// <summary>BiRefNet-lite salient object segmentation at 512×512 (Select Subject).</summary>
    public const string SubjectFile = "birefnet-lite-512.onnx";

    /// <summary>How to get the models, for notices and skipped tests.</summary>
    public const string FetchHint =
        "The AI selection models are not installed. From the Strayta source folder run `dotnet build tools/FetchModels.proj` " +
        "(downloads about 335 MB into models/), then rebuild.";

    public SegmentationModels(string? directory) => Directory = directory;

    /// <summary>The folder holding the model files, or null if none was found.</summary>
    public string? Directory { get; }

    public string? SamEncoderPath => PathIfExists(SamEncoderFile);
    public string? SamDecoderPath => PathIfExists(SamDecoderFile);
    public string? SubjectPath => PathIfExists(SubjectFile);

    public bool HasObjectModel => SamEncoderPath is not null && SamDecoderPath is not null;
    public bool HasSubjectModel => SubjectPath is not null;

    private string? PathIfExists(string file) =>
        Directory is { } dir && File.Exists(Path.Combine(dir, file)) ? Path.Combine(dir, file) : null;

    /// <summary>
    /// Finds the models: $STRAYTA_MODELS, then models/ next to the app (where the build copies them), then a
    /// models/ folder in any parent directory (a source checkout, for tests and <c>dotnet run</c>).
    /// </summary>
    public static SegmentationModels Locate()
    {
        if (Environment.GetEnvironmentVariable("STRAYTA_MODELS") is { Length: > 0 } env) return new SegmentationModels(env);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "models");
            if (File.Exists(Path.Combine(candidate, SamEncoderFile)) || File.Exists(Path.Combine(candidate, SubjectFile)))
                return new SegmentationModels(candidate);
        }
        return new SegmentationModels(null);
    }
}
