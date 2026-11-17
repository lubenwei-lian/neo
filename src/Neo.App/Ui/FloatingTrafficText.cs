using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace Neo.App.Ui;

/// <summary>
/// Tiny source-font atlas for the fixed labels and changing numbers on the
/// transparent surface. This preserves the reference's font stack and weights
/// without a browser or a second text-rendering runtime in the application.
/// Arbitrary node names remain normal system text.
/// </summary>
internal sealed class FloatingTrafficText : IDisposable
{
    private readonly record struct Glyph(RectangleF Source, float Advance);
    private readonly Dictionary<string,Glyph> _glyphs = new(StringComparer.Ordinal);
    private readonly SortedDictionary<double,string> _atlases=new();
    private Bitmap? _image;
    private double _pixels;

    internal FloatingTrafficText(string directory)
    {
        foreach(var dpi in new[]{1d,1.25,1.5,1.75,2,2.5,3})
        {
            var path=Path.Combine(directory,"text-"+dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)+"x.png");
            if(File.Exists(path))_atlases.Add(dpi,path);
        }
        if(!_atlases.ContainsKey(1) || !_atlases.ContainsKey(2))throw new InvalidDataException("Floating text atlas is incomplete.");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"text.json")));
        foreach (var item in json.RootElement.GetProperty("entries").EnumerateArray())
            _glyphs.Add(item.GetProperty("key").GetString()!, new(
                new(item.GetProperty("x").GetSingle(),item.GetProperty("y").GetSingle(),
                    item.GetProperty("width").GetSingle(),item.GetProperty("height").GetSingle()),
                item.GetProperty("advance").GetSingle()));
    }

    internal float Measure(string kind, string text)
    {
        if (_glyphs.TryGetValue(kind+":"+text,out var whole)) return whole.Advance;
        var width=0f;
        foreach (var ch in text)
        {
            if (!_glyphs.TryGetValue(kind+":"+ch,out var glyph)) return -1;
            width+=glyph.Advance;
        }
        return width;
    }

    internal bool Draw(Graphics g,string kind,string text,RectangleF bounds,StringAlignment alignment,double scale)
    {
        var width=Measure(kind,text);
        if(width<0 || width>bounds.Width) return false;
        var x=bounds.X+(alignment==StringAlignment.Far?bounds.Width-width:alignment==StringAlignment.Center?(bounds.Width-width)/2:0);
        var pixels=_atlases.Keys.FirstOrDefault(dpi=>dpi>=scale,_atlases.Keys.Last());
        if(_image==null || _pixels!=pixels)
        {
            var image=new Bitmap(_atlases[pixels]);
            _image?.Dispose();_image=image;_pixels=pixels;
        }
        var interpolation=g.InterpolationMode; var offset=g.PixelOffsetMode;
        // Common fractional DPI scales have their own rasterization. Atlas cells
        // lie on a four-DIP grid, so their source bounds are whole device pixels.
        if(Math.Abs(scale-pixels)<.001) { g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half; }
        void Paint(Glyph glyph)
        {
            var src=glyph.Source;
            var y=bounds.Y+(bounds.Height-src.Height)/2;
            var target=new RectangleF((float)(Math.Round(x*scale)/scale),(float)(Math.Round(y*scale)/scale),src.Width,src.Height);
            g.DrawImage(_image,target,new RectangleF((float)(src.X*pixels),(float)(src.Y*pixels),(float)(src.Width*pixels),(float)(src.Height*pixels)),GraphicsUnit.Pixel);
            x+=glyph.Advance;
        }
        if(_glyphs.TryGetValue(kind+":"+text,out var whole)) Paint(whole);
        else foreach(var ch in text) Paint(_glyphs[kind+":"+ch]);
        g.InterpolationMode=interpolation;g.PixelOffsetMode=offset;
        return true;
    }

    public void Dispose(){_image?.Dispose();_image=null;_atlases.Clear();}
}
