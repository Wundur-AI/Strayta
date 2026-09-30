using Strayta.Psd;

namespace Strayta.Inspect;

/// <summary>`channels`: lists the saved selections and spot channels of a file or of every file under a folder.</summary>
internal static class ChannelCommands
{
    public static int Dump(string target)
    {
        IEnumerable<string> files = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")).Order()
            : [target];
        int withChannels = 0;
        foreach (var path in files)
        {
            PsdFile file;
            try
            {
                file = PsdFile.Open(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileName(path)}: {ex.Message}");
                continue;
            }
            var channels = PsdChannels.Read(file);
            if (channels.Count == 0) continue;
            withChannels++;
            Console.WriteLine($"{Path.GetFileName(path)}  {file.Header.ColorMode} {file.Header.BitDepth}-bit, transparency {file.CompositeHasTransparency}");
            foreach (var c in channels)
            {
                var color = c.SourceColor is PsdStoredColor s ? $"space {s.ColorSpace} [{string.Join(",", s.Components)}]" : "rgb";
                double mean = Enumerable.Range(0, c.Pixels.Width * c.Pixels.Height).Average(i => c.Pixels.GetNormalized(i));
                Console.WriteLine($"    #{c.Id} \"{c.Name}\" {c.Kind} color ({c.Color.R:F2},{c.Color.G:F2},{c.Color.B:F2}) {color} opacity {c.Opacity:P0} mean {mean:F3}");
            }
        }
        Console.WriteLine($"{withChannels} file(s) with saved selections or spot channels");
        return 0;
    }
}
