using Strayta.Core;

namespace Strayta.Psd.Tests;

public class FlatPsdTests
{
    /// <summary>
    /// A PSD with no layer records keeps its image only in the composite. Opened for editing it must become a
    /// Background layer, or edits start from an empty canvas and saving writes a blank image.
    /// </summary>
    [Fact]
    public void FlatFileOpensAsBackgroundAndSurvivesSaving()
    {
        var bytes = new PsdTestBuilder(2, 1) { Composite = [[10, 20], [30, 40], [50, 60]] }.Build();
        var path = Path.Combine(Path.GetTempPath(), $"strayta-flat-{Guid.NewGuid():N}.psd");
        try
        {
            File.WriteAllBytes(path, bytes);
            var doc = PsdFile.OpenForEditing(path);
            var layer = Assert.IsType<PixelLayer>(Assert.Single(doc.Root.Children));
            Assert.Equal("Background", layer.Name);
            Assert.Equal(doc.Bounds, layer.Bounds);
            Assert.Equal([10, 20], layer.Pixels!.ColorPlanes[0].Data);

            using (var stream = File.Create(path)) PsdWriter.Write(doc, stream);
            var saved = PsdFile.OpenForEditing(path);
            var again = Assert.IsType<PixelLayer>(Assert.Single(saved.Root.Children));
            Assert.Equal([30, 40], again.Pixels!.ColorPlanes[1].Data);
            Assert.Equal([50, 60], Assert.IsType<Raster>(saved.Composite).ColorPlanes[2].Data);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
