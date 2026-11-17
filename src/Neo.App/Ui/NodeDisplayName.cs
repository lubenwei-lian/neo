using System.Text;

namespace Neo.App.Ui;

public static class NodeDisplayName
{
    public static string WithoutFlag(string name)
    {
        var text = name.TrimStart();
        var count = 0;
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is < 0x1F1E6 or > 0x1F1FF) break;
            length += rune.Utf16SequenceLength;
            if (++count == 2) return text[length..].TrimStart(' ', '\uFE0F');
        }
        return name;
    }
}
