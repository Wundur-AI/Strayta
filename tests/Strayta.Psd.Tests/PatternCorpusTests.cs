namespace Strayta.Psd.Tests;

/// <summary>Pattern blocks in real files ($STRAYTA_CORPUS) parse and survive a save unchanged.</summary>
public class PatternCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusTests.Files), MemberType = typeof(CorpusTests))]
    public void Pattern_blocks_round_trip_byte_for_byte(string relativePath)
    {
        string? dir = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");
        if (relativePath.Length == 0 || dir is null) Assert.Skip("Set STRAYTA_CORPUS to a folder of PSD files to run corpus tests.");
        var options = new PsdReadOptions { MaxRawBlockBytes = long.MaxValue, SkipComposite = true };
        var original = PsdFile.Open(Path.Combine(dir, relativePath), options);
        var doc = original.ToDocument();
        _ = PsdPatterns.References(original);
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var again = PsdFile.Read(ms, options);
        var before = original.GlobalBlocks.Where(b => PsdPatterns.IsPatternKey(b.Key)).Select(b => (b.Key, b.Data)).ToList();
        var after = again.GlobalBlocks.Where(b => PsdPatterns.IsPatternKey(b.Key)).Select(b => (b.Key, b.Data)).ToList();
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Key, after[i].Key);
            Assert.Equal(before[i].Data, after[i].Data);
        }
        Assert.Equal(doc.Patterns.Count, again.ToDocument().Patterns.Count);
    }
}
