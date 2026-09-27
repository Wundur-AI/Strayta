namespace Strayta.Psd.Tests;

/// <summary>
/// Parses every PSD/PSB under $STRAYTA_CORPUS. The corpus holds licensed files that cannot live in
/// the repo, so these tests are skipped when the variable is not set.
/// </summary>
public class CorpusTests
{
    private static readonly string? CorpusDir = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        if (CorpusDir is null || !Directory.Exists(CorpusDir))
        {
            data.Add("");
            return data;
        }
        foreach (var f in Directory.EnumerateFiles(CorpusDir, "*.ps?", SearchOption.AllDirectories))
            if (!Path.GetFileName(f).StartsWith("._"))
                data.Add(Path.GetRelativePath(CorpusDir, f));
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Parses(string relativePath)
    {
        if (relativePath.Length == 0) Assert.Skip("Set STRAYTA_CORPUS to a folder of PSD files to run corpus tests.");

        var file = PsdFile.Open(Path.Combine(CorpusDir!, relativePath));
        var doc = file.ToDocument();

        Assert.Equal(file.Header.Width, doc.Width);
        if (file.HasRealMergedData != false)
            Assert.NotNull(doc.Composite);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Round_trips_without_changes(string relativePath)
    {
        if (relativePath.Length == 0) Assert.Skip("Set STRAYTA_CORPUS to a folder of PSD files to run corpus tests.");

        var options = new PsdReadOptions { MaxRawBlockBytes = long.MaxValue };
        var original = PsdFile.Open(Path.Combine(CorpusDir!, relativePath), options);
        var ms = new MemoryStream();
        PsdWriter.Write(original.ToDocument(), ms);
        ms.Position = 0;
        var again = PsdFile.Read(ms, options);

        Assert.Equal(original.Layers.Count, again.Layers.Count);
        for (int i = 0; i < original.Layers.Count; i++)
        {
            var (a, b) = (original.Layers[i], again.Layers[i]);
            Assert.Equal(a.Name, b.Name);
            foreach (var (id, plane) in a.ChannelData)
                Assert.Equal(plane.Data, b.ChannelData[id].Data);
            var keep = (PsdLayerRecord r) => r.Blocks.Where(k => k.Key is not ("luni" or "lsct" or "lsdk" or "iOpa")).Select(k => (k.Key, k.Data)).ToList();
            Assert.Equal(keep(a).Select(k => k.Key), keep(b).Select(k => k.Key));
        }
    }
}
