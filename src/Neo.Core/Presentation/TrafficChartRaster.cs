namespace Neo.Core.Presentation;

/// <summary>Bounded, reusable BGRA image for the existing two-band 8-level traffic chart.</summary>
public sealed class TrafficChartRaster
{
    public const int Rows = 8;
    public const double Cell = 9, Gap = 2, Left = 10, BandGap = 20;
    private int[] _down = [], _up = [];
    private double _scale, _height;
    public byte[] Pixels { get; private set; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Columns => _down.Length;
    public bool Dirty { get; private set; }

    public bool Configure(int columns, double height, double scale)
    {
        if (columns is < 1 or > 2048 || !double.IsFinite(height) || height is < 1 or > 4096
            || !double.IsFinite(scale) || scale is < 0.5 or > 4) throw new ArgumentOutOfRangeException();
        var width = checked((int)Math.Ceiling((Left + columns * (Cell + Gap)) * scale));
        var pixelHeight = checked((int)Math.Ceiling(height * scale));
        var bytes = checked((long)width * pixelHeight * 4);
        if (bytes > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(columns), "Chart image exceeds its memory budget");
        if (columns == Columns && height == _height && scale == _scale) return false;
        Width = width; Height = pixelHeight; _height = height; _scale = scale;
        Pixels = new byte[(int)bytes]; _down = new int[columns]; _up = new int[columns];
        Array.Fill(_down, -1); Array.Fill(_up, -1); Dirty = true;
        return true;
    }

    public void SetColumn(int column, int down, int up, ReadOnlySpan<uint> downPalette, ReadOnlySpan<uint> upPalette)
    {
        if ((uint)column >= (uint)Columns || down is < 0 or > Rows || up is < 0 or > Rows
            || downPalette.Length < 9 || upPalette.Length < 9) throw new ArgumentOutOfRangeException();
        var bandHeight = Rows * (Cell + Gap) - Gap;
        var top = Math.Max(4, (_height - (bandHeight * 2 + BandGap)) / 2);
        if (_down[column] != down)
        {
            DrawBand(column, down, top, downPalette); _down[column] = down; Dirty = true;
        }
        if (_up[column] != up)
        {
            DrawBand(column, up, top + bandHeight + BandGap, upPalette); _up[column] = up; Dirty = true;
        }
    }

    private void DrawBand(int column, int level, double top, ReadOnlySpan<uint> palette)
    {
        var left = (int)Math.Round((Left + column * (Cell + Gap)) * _scale);
        var size = Math.Max(1, (int)Math.Round(Cell * _scale));
        for (var row = 0; row < Rows; row++)
        {
            var y = (int)Math.Round((top + (Rows - 1 - row) * (Cell + Gap)) * _scale);
            var color = palette[level > 0 && row == level - 1 ? level : 0];
            for (var py = 0; py < size; py++)
            {
                var targetY = y + py;
                if ((uint)targetY >= (uint)Height) continue;
                var dy = (py + 0.5) / _scale;
                var cy = Math.Clamp(dy, 2, Cell - 2);
                for (var px = 0; px < size; px++)
                {
                    var targetX = left + px;
                    if ((uint)targetX >= (uint)Width) continue;
                    var dx = (px + 0.5) / _scale;
                    var cx = Math.Clamp(dx, 2, Cell - 2);
                    if ((dx - cx) * (dx - cx) + (dy - cy) * (dy - cy) > 4) continue;
                    var offset = (targetY * Width + targetX) * 4;
                    Pixels[offset] = (byte)color; Pixels[offset + 1] = (byte)(color >> 8);
                    Pixels[offset + 2] = (byte)(color >> 16); Pixels[offset + 3] = (byte)(color >> 24);
                }
            }
        }
    }

    public void MarkUploaded() => Dirty = false;
    public void Release()
    {
        Pixels = []; _down = []; _up = []; Width = Height = 0; _height = _scale = 0; Dirty = false;
    }
}
