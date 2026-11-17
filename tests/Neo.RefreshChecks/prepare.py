"""Compile unchanged production UI methods with counted UI boundaries, never a live window."""
from pathlib import Path
import argparse
import hashlib
import json

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--baseline', default='')
args = parser.parse_args()
BASELINE = Path(args.baseline) if args.baseline else ROOT
OUTPUT = HERE / 'obj'
OUTPUT.mkdir(exist_ok=True)
bindings = {}

def read(root, path):
    file = root / path
    source = file.read_text(encoding='utf-8-sig')
    bindings[str(file)] = hashlib.sha256(file.read_bytes()).hexdigest()
    return source

def method(source, signature):
    start = source.index('    ' + signature)
    end = source.index('\n    }', start) + len('\n    }')
    return source[start:end]

def memory_property(source):
    signature = '    public static int? CoreMemoryMb'
    start = source.index(signature)
    if '=>' in source[start:source.index('\n', start)]:
        return source[start:source.index(';', start) + 1]
    return method(source, signature.strip())

parts = ['''#nullable enable
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Neo.App.Services;
''']
for prefix, root in [('', ROOT), ('Baseline', BASELINE)]:
    chart = read(root, 'src/Neo.App/Pages/ConnectionPage.xaml.cs')
    log = read(root, 'src/Neo.App/Pages/LogPage.xaml.cs')
    connection = read(root, 'src/Neo.App/Services/ConnectionService.cs')
    if 'TrafficChartRaster' in chart:
        parts.append('namespace Neo.RefreshChecks { public sealed class ' + prefix + '''Chart
        {
            public Neo.Core.Presentation.TrafficChartRaster Raster { get; } = new();
            public void Layout(int cols, double height = 250) { if (Raster.Configure(cols, height, 1)) UiWrites.Count++; }
            public void Invalidate() { }
            public object First => Raster.Pixels;
            public int Count => Raster.Pixels.Length;
        } }''')
    else:
        parts.append('namespace Neo.RefreshChecks { public sealed class ' + prefix + '''Chart
        {
            private const int ChartRows = 8;
            private const double CellSize = 9, CellGap = 2, BandGap = 20, PlotLeft = 10;
            private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _dnCells = new();
            private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _upCells = new();
            private int _cellsCols;
            private int _layoutCols = -1;
            private bool _layoutDirty = true;
            private int[]? _dnLvCache, _upLvCache;
            private readonly ChartHost ChartCanvas = new();
            private readonly object UpBandLabel = new();
            public void Layout(int cols, double height = 250) => EnsureCells(cols, height);
            public void Invalidate() => _layoutDirty = true;
            public object First => _dnCells[0];
            public int Count => _dnCells.Count + _upCells.Count;
            public bool NewCellsUncolored => _dnLvCache!.All(x => x == -1) && _upLvCache!.All(x => x == -1);
    ''' + method(chart, 'private void EnsureCells(') + '\n} }')
    fields = log[log.index('    private string _level'):log.index('    public LogPage()')]
    parts.append('namespace Neo.RefreshChecks { using App = global::Neo.App.App; public sealed class ' + prefix + 'LogView {\n'
        + fields + '''
        public DispatcherQueue DispatcherQueue { get; } = new();
        public FakeLogList LogList { get; } = new();
        public bool TimerRunning => _flush.IsRunning;
        public int PendingCount => _pending.Count;
        public ''' + prefix + '''LogView() { _flush = new(); }
        public void Post(LogEntry entry) => OnAppended(entry);
        public void Tick() { if (_flush.IsRunning) FlushPending(); }
        public void ClearView() => OnClearClick(this, new RoutedEventArgs());
        private bool Match(LogEntry entry) => _level == "All" || entry.Level == _level;
        private void UpdateEmpty() { }
        private void Rebuild() => LogList.Items.Clear();
''' + '\n'.join(method(log, signature) for signature in [
            'private void OnAppended(', 'private void FlushPending(', 'private void TrimList(',
            'public void Release(', 'private void OnClearClick(']) + '\n} }')
    parts.append('namespace Neo.App.Services { public static ' + ('partial ' if not prefix else '')
        + 'class ' + prefix + 'ConnectionService {\n'
        + ('private static readonly FakeCore _xray = new();\n' if prefix else '')
        + memory_property(connection) + '\n} }')

baseline_stats = read(BASELINE, 'src/Neo.App/Services/XrayStats.cs')
parts.append('namespace Neo.RefreshChecks {\n' + baseline_stats.replace('namespace Neo.App.Services;', '')
    .replace('public static class XrayStats', 'public static class BaselineXrayStats') + '\n}')
parts.append('namespace Neo.RefreshChecks { public static class SourceBinding { public const bool HasBaseline = '
    + ('true' if args.baseline else 'false') + '; } }')
(OUTPUT / 'RefreshUnderTest.g.cs').write_text('\n'.join(parts), encoding='utf-8')
read(ROOT, 'src/Neo.Core/Presentation/TrafficChartRaster.cs')
for name in ('StatsSampler.cs', 'XrayStats.cs'):
    read(ROOT, 'src/Neo.App/Services/' + name)
(OUTPUT / 'source-binding.json').write_text(json.dumps({
    'files': bindings,
    'boundary': 'Production StatsSampler, XrayStats and TrafficChartRaster are linked in full. Raster layout uses a thin counted adapter; legacy EnsureCells and log append/flush/trim/clear/release '
        'and the public memory getter are extracted unchanged. UI objects count setters; the dispatcher is a controlled '
        'queue; stats endpoints are synthetic; memory sampling reads this test process. Baseline receives only a class '
        'rename and identical UI boundaries. This is not WinUI rendering, public network or installed-app measurement.'
}, indent=2, ensure_ascii=False), encoding='utf-8')
