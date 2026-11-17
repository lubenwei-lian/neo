using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Neo.App.Ui;

internal sealed record FloatingTrafficData(bool Connected, bool Connecting, string Node,
    double? DownloadBps, long Up, long Down, double SubscriptionUsed, long SubscriptionTotal, double? UploadBps=null);

/// <summary>
/// Small per-pixel-alpha tool windows. Source CSS surfaces are captured at 2x from
/// floatball.html; the native layer draws only live text and the SVG-equivalent arc.
/// Frames run only during finite transitions; the statistics path reads a cached sample.
/// </summary>
internal sealed class FloatingTrafficWindow : IDisposable
{
    private const int BallMargin=48, PanelMargin=64;
    private readonly DispatcherQueue _queue;
    private readonly Func<FloatingTrafficData> _read;
    private readonly Action _openMain, _toggleConnection;
    private readonly Action<Exception> _onFailure;
    private readonly Action<int,int> _savePosition;
    private readonly DispatcherQueueTimer _sample, _frames, _input, _click;
    private readonly Layer _ball, _panel;
    private readonly Bitmap _normal, _hover, _panelBase;
    private readonly FloatingTrafficText _sourceText;
    private readonly Dictionary<(string,float,FontStyle),Font> _fonts=new();
    private readonly StringFormat _textFormat=new(StringFormat.GenericTypographic) { FormatFlags=StringFormatFlags.NoWrap, Trimming=StringTrimming.EllipsisCharacter, LineAlignment=StringAlignment.Center };
    private FloatingTrafficData _data=new(false,false,"未选择节点",null,0,0,0,0);
    private FloatingTrafficData? _reviewData;
    private Bitmap? _panelContent;
    private Bitmap? _ballNormalCache,_ballHoverCache;
    private (string Value,string Unit) _cachedFace;
    private readonly record struct PanelContentKey(bool Connected,bool Connecting,string Node,string Upload,string Download,string Quota,string Up,string Down,int QuotaPixels,bool HasQuota,bool Hover);
    private PanelContentKey? _cachedPanel;
    private bool _ballDirty=true, _panelDirty=true, _ballAnimating, _panelAnimating;
    private bool _animationsEnabled=ReadAnimationsEnabled();
    private double _scale=1, _arcFrom, _arcTo, _hoverFrom, _hoverTo, _hoverValue;
    private double _arcAt=-1000, _hoverAt=-1000, _panelAt=-1000, _panelFrom, _panelValue, _opacityFrom, _opacity;
    private Color _strokeFrom=Accent,_strokeTo=Accent;
    private double _strokeAt=-1000;
    private bool _panelOpen, _disposed, _failed, _dragging, _moved, _double, _panelPressed, _buttonHover;
    private Point _down, _origin, _lastClick;
    private int _x, _y;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private long _renderedFrames;
    private double _frameCostMs;
    private double _lastFrameAt;
    private readonly List<double>? _frameIntervals=NativeUiReview.Enabled?new():null;
    private bool _mouseWasDown;
    private bool _failNextFrameForReview;
    private double? _reviewScale;
    private long _ballPresents, _panelPresents, _faceBuilds, _panelBuilds, _layoutPasses;
    private double Now => _clock.Elapsed.TotalMilliseconds;
    private double PanelHeight => _data.SubscriptionTotal>0?231:209;
    private float ButtonY => _data.SubscriptionTotal>0?248:226;
    private static readonly Color Text1=Color.FromArgb(236,236,236), Text2=Color.FromArgb(154,154,160), Text3=Color.FromArgb(110,110,118);
    private static readonly Color Accent=Color.FromArgb(90,169,255), Yellow=Color.FromArgb(240,169,59);

    internal FloatingTrafficWindow(DispatcherQueue queue,Func<FloatingTrafficData> read,Action openMain,Action toggleConnection,Action<int,int> savePosition,Action<Exception> onFailure)
    {
        _queue=queue; _read=read; _openMain=openMain; _toggleConnection=toggleConnection; _savePosition=savePosition; _onFailure=onFailure;
        var dir=Path.Combine(AppContext.BaseDirectory,"Assets","floatball");
        _normal=new Bitmap(Path.Combine(dir,"ball.png")); _hover=new Bitmap(Path.Combine(dir,"ball-hover.png")); _panelBase=new Bitmap(Path.Combine(dir,"panel.png"));
        _sourceText=new FloatingTrafficText(dir);
        _ball=new Layer(this,false); _panel=new Layer(this,true);
        _sample=queue.CreateTimer(); _sample.Interval=TimeSpan.FromSeconds(1); _sample.Tick+=(_,_)=>RunCallback(Refresh);
        // Below the default Windows timer quantum: actual cadence is measured in review,
        // without raising the system-wide timer resolution or keeping a perpetual loop.
        _frames=queue.CreateTimer(); _frames.Interval=TimeSpan.FromMilliseconds(8); _frames.Tick+=(_,_)=>RunCallback(Frame);
        _input=queue.CreateTimer(); _input.Interval=TimeSpan.FromMilliseconds(33); _input.Tick+=(_,_)=>RunCallback(PollPanelInput);
        _click=queue.CreateTimer(); _click.IsRepeating=false; _click.Interval=TimeSpan.FromMilliseconds(Native.GetDoubleClickTime());
        // Keep only the double-click recognition window. The first release
        // expands immediately, matching the source rather than adding 500ms lag.
    }

    private void RunCallback(Action action)
    {
        if (_disposed || _failed || App.IsShuttingDown) return;
        try { action(); }
        catch (Exception ex) { Fail(ex); }
    }

    private void QueueAction(Action action) => _queue.TryEnqueue(() => RunCallback(action));

    private void Fail(Exception error)
    {
        if (_disposed || _failed) return;
        _failed=true;
        _sample.Stop(); _frames.Stop(); _input.Stop(); _click.Stop();
        // A timer delegate failure becomes a native CoreMessaging fail-fast.
        // Contain it here and tear down this optional surface after the current
        // native callback unwinds, without touching the connection or core.
        _queue.TryEnqueue(() =>
        {
            if (_disposed) return;
            try { _onFailure(error); }
            catch { Dispose(); }
        });
    }

    internal void Show(int x,int y)
    {
        var monitor=Native.MonitorFromPoint(new(x==int.MinValue?0:x,y==int.MinValue?0:y),2);
        var work=Native.Work(monitor);
        _x=x==int.MinValue?work.Left+(int)Math.Round((work.Right-work.Left)*.7):x;
        _y=y==int.MinValue?work.Top+(int)Math.Round((work.Bottom-work.Top)*.62):y;
        Constrain(); Refresh(); _sample.Start();
        _ball.Show();
    }

    private void Constrain()
    {
        _layoutPasses++;
        var monitor=Native.MonitorFromPoint(new(_x+32,_y+32),2);
        var work=Native.Work(monitor);
        Native.GetDpiForMonitor(monitor,0,out var dpi,out _);
        var scale=_reviewScale ?? (dpi>0?dpi/96d:1);
        if (Math.Abs(scale-_scale)>.001)
        {
            _scale=scale; InvalidatePanel();ReleaseFaces();_ballDirty=true;
        }
        var size=(int)Math.Ceiling(64*_scale); var inset=(int)Math.Ceiling(8*_scale);
        _x=Math.Clamp(_x,work.Left+inset,Math.Max(work.Left+inset,work.Right-size-inset));
        _y=Math.Clamp(_y,work.Top+inset,Math.Max(work.Top+inset,work.Bottom-size-inset));
        _ball.Resize((int)Math.Ceiling(160*_scale),(int)Math.Ceiling(160*_scale));
        _ball.Move(_x-(int)(BallMargin*_scale),_y-(int)(BallMargin*_scale));
        PositionPanel();
    }

    private void PositionPanel()
    {
        var work=Native.Work(Native.MonitorFromPoint(new(_x+32,_y+32),2));
        var pw=(int)Math.Ceiling(236*_scale); var ph=(int)Math.Ceiling(PanelHeight*_scale);
        var x=Math.Clamp(_x+(int)(32*_scale)-pw/2,work.Left+(int)(12*_scale),Math.Max(work.Left+(int)(12*_scale),work.Right-pw-(int)(12*_scale)));
        var below=_y+(int)(74*_scale);
        var y=below+ph<=work.Bottom-(int)(12*_scale)?below:_y-ph-(int)(10*_scale);
        y=Math.Clamp(y,work.Top+(int)(8*_scale),Math.Max(work.Top+(int)(8*_scale),work.Bottom-ph-(int)(8*_scale)));
        _panel.Resize((int)Math.Ceiling(364*_scale),(int)Math.Ceiling((PanelHeight+128)*_scale));
        _panel.Move(x-(int)(PanelMargin*_scale),y-(int)(PanelMargin*_scale));
    }

    private void Refresh()
    {
        if (_disposed || App.IsShuttingDown) return;
        var previousQuota=_data.SubscriptionTotal>0;
        _data=_reviewData ?? _read();
        var p=_data.Connected && _data.DownloadBps is { } speed?Math.Min(1,Math.Pow(Math.Max(0,speed)/(20*1024*1024d),.4)):0;
        if(!double.IsFinite(p))p=0;
        // Ignore sub-quarter-pixel ring changes; numeric text has its own display key.
        p=Math.Round(p*720*_scale)/(720*_scale);
        var colour=p>.78?Yellow:Accent;
        if(colour!=_strokeTo){_strokeFrom=StrokeAt(Now);_strokeTo=colour;_strokeAt=Now;_ballDirty=true;StartBallAnimation();}
        if (Math.Abs(p-_arcTo)>.0001)
        {
            _arcFrom=ArcAt(Now); _arcTo=p; _arcAt=Now;
            _ballDirty=true;StartBallAnimation();
        }
        if(previousQuota!=(_data.SubscriptionTotal>0)){PositionPanel();InvalidatePanel();}
        if(_ballNormalCache==null || _cachedFace!=FaceKey())_ballDirty=true;
        if(_cachedPanel!=PanelKey())InvalidatePanel();
        if(_ballDirty)DrawBall();
        if ((_panelOpen || _panelValue>0) && _panelDirty) DrawPanel();
    }

    internal void RefreshNow()=>RunCallback(Refresh);
    private static bool ReadAnimationsEnabled()
    {
        return !Native.SystemParametersInfo(0x1042,0,out var enabled,0) || enabled;
    }
    private bool AnimationsEnabled=>_animationsEnabled;
    private void StartFrames(){if(!_frames.IsRunning)_lastFrameAt=0;_frames.Start();}
    private void StartBallAnimation(){if(AnimationsEnabled){_ballAnimating=true;StartFrames();}}
    private double ArcAt(double now)=>_arcFrom+(_arcTo-_arcFrom)*Math.Clamp((now-_arcAt)/450,0,1);
    private void Frame()
    {
        if (_disposed || App.IsShuttingDown) { _frames.Stop(); return; }
        if (_failNextFrameForReview) { _failNextFrameForReview=false; throw new InvalidOperationException("Injected floating frame failure for isolated regression"); }
        var started=Stopwatch.GetTimestamp(); var now=Now;
        if(_frameIntervals!=null && _lastFrameAt>0)_frameIntervals.Add(now-_lastFrameAt);
        _lastFrameAt=now;
        if(_ballAnimating)
        {
            _hoverValue=AnimationsEnabled?_hoverFrom+(_hoverTo-_hoverFrom)*Ease(Math.Clamp((now-_hoverAt)/180,0,1),.25,.1,.25,1):_hoverTo;
            DrawBall();
            _ballAnimating=AnimationsEnabled && (now-_arcAt<450 || now-_hoverAt<180 || now-_strokeAt<300);
        }
        if(_panelAnimating)
        {
            UpdatePanelProgress(now);DrawPanel();
            _panelAnimating=AnimationsEnabled && now-_panelAt<200;
            if(!_panelAnimating && !_panelOpen)_panel.Hide();
        }
        _renderedFrames++; _frameCostMs+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!_ballAnimating && !_panelAnimating)_frames.Stop();
    }
    // cubic-bezier(.22,.9,.24,1), matching the prototype panel transform.
    private static double Ease(double x,double x1=.22,double y1=.9,double x2=.24,double y2=1)
    {
        if(x<=0)return 0;if(x>=1)return 1;
        var lo=0d;var hi=1d;
        for(var i=0;i<14;i++) { var t=(lo+hi)/2;var u=1-t;var bx=3*u*u*t*x1+3*u*t*t*x2+t*t*t;if(bx<x)lo=t;else hi=t; }
        var v=(lo+hi)/2;var w=1-v;return 3*w*w*v*y1+3*w*v*v*y2+v*v*v;
    }
    private Color StrokeAt(double now)
    {
        var p=Ease(Math.Clamp((now-_strokeAt)/300,0,1),.25,.1,.25,1);
        return Color.FromArgb((int)Math.Round(_strokeFrom.R+(_strokeTo.R-_strokeFrom.R)*p),(int)Math.Round(_strokeFrom.G+(_strokeTo.G-_strokeFrom.G)*p),(int)Math.Round(_strokeFrom.B+(_strokeTo.B-_strokeFrom.B)*p));
    }

    private void DrawBall()
    {
        var face=FaceKey();
        if(_ballNormalCache==null || _cachedFace!=face)
        {
            ReleaseFaces();_cachedFace=face;
            _ballNormalCache=BuildFace(_normal);_ballHoverCache=BuildFace(_hover);
        }
        var g=_ball.Begin(1);
        g.DrawImageUnscaled(_ballNormalCache,0,0);
        if (_hoverValue>0)
        {
            using var opacity=new ImageAttributes(); opacity.SetColorMatrix(new ColorMatrix { Matrix33=(float)_hoverValue });
            g.DrawImage(_ballHoverCache!,new Rectangle(0,0,_ball.Width,_ball.Height),0,0,_ball.Width,_ball.Height,GraphicsUnit.Pixel,opacity);
        }
        g.ScaleTransform((float)_scale,(float)_scale);
        var p=AnimationsEnabled?ArcAt(Now):_arcTo;
        if (_data.Connected && p>0)
        {
            var colour=AnimationsEnabled?StrokeAt(Now):_strokeTo;
            DrawProgressArc(g,colour,p);
        }
        _ball.Present();
        _ballPresents++;_ballDirty=false;
    }

    internal static void DrawProgressArc(Graphics g,Color colour,double progress)
    {
        if (!double.IsFinite(progress) || progress<=0) return;
        var sweep=(float)(360*Math.Min(progress,1));
        if (sweep<1)
        {
            // GDI+ rejects some very short round-capped arcs (E_UNEXPECTED).
            // Below half a pixel of arc length the two caps overlap: draw that
            // same cap directly, at the arc midpoint, rather than a degenerate arc.
            var angle=(-90+sweep/2)*Math.PI/180;
            using var brush=new SolidBrush(colour);
            g.FillEllipse(brush,(float)(80+29*Math.Cos(angle)-1.5),(float)(80+29*Math.Sin(angle)-1.5),3,3);
            return;
        }
        using var pen=new Pen(colour,3) { StartCap=LineCap.Round,EndCap=LineCap.Round };
        g.DrawArc(pen,51,51,58,58,-90,sweep);
    }

    private Bitmap BuildFace(Bitmap background)
    {
        _faceBuilds++;
        var result=new Bitmap(_ball.Width,_ball.Height,PixelFormat.Format32bppPArgb);
        using var g=Graphics.FromImage(result);Configure(g);g.ScaleTransform((float)_scale,(float)_scale);
        g.DrawImage(background,new RectangleF(0,0,160,160));
        using var track=new Pen(Color.FromArgb(18,255,255,255),3);g.DrawEllipse(track,51,51,58,58);
        var (value,unit)=FaceKey();
        var w=_sourceText.Measure("ball",value);
        var uw=unit.Length==0?0:_sourceText.Measure("unit",unit)+1;
        var left=80-(w+uw)/2;
        Text(g,value,left,71,w+1,18,15,Text1,FontStyle.Bold,atlas:"ball");
        if(unit.Length>0)Text(g,unit,left+w+1,74,uw+1,13,9.5f,Text3,atlas:"unit");
        return result;
    }

    private void ReleaseFaces(){_ballNormalCache?.Dispose();_ballHoverCache?.Dispose();_ballNormalCache=_ballHoverCache=null;}

    private (string Value,string Unit) FaceKey()
    {
        if(!_data.Connected || _data.DownloadBps is not { } speed || !double.IsFinite(speed))return ("—","");
        speed=Math.Max(0,speed);
        return speed<1024*1024?(Math.Round(speed/1024).ToString("0",System.Globalization.CultureInfo.InvariantCulture),"K"):
            ((speed/1024/1024).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture),"M");
    }
    private string Rate(double? speed)=>_data.Connected && speed is { } value && double.IsFinite(value)?Services.UsageTracker.Format((long)Math.Clamp(value,0,long.MaxValue))+"/s":"—";
    private PanelContentKey PanelKey()
    {
        var hasQuota=_data.SubscriptionTotal>0;
        var used=double.IsFinite(_data.SubscriptionUsed)?Math.Clamp(_data.SubscriptionUsed,0,long.MaxValue):0;
        return new(_data.Connected,_data.Connecting,_data.Node,Rate(_data.UploadBps),Rate(_data.DownloadBps),
            hasQuota?Services.UsageTracker.Format((long)used)+" / "+Services.UsageTracker.Format(_data.SubscriptionTotal):"—",
            Services.UsageTracker.Format(_data.Up),Services.UsageTracker.Format(_data.Down),
            hasQuota?(int)Math.Round(206*_scale*Math.Clamp(used/_data.SubscriptionTotal,0,1)):0,hasQuota,_buttonHover);
    }
    private void InvalidatePanel(){_panelDirty=true;_cachedPanel=null;}

    private void BuildPanel()
    {
        _panelBuilds++;var key=PanelKey();_cachedPanel=key;
        _panelContent?.Dispose(); _panelContent=new Bitmap(_panel.Width,_panel.Height,PixelFormat.Format32bppPArgb);
        using var g=Graphics.FromImage(_panelContent);Configure(g);
        // Nine-slice keeps the source's 18px corners and 40px shadow when the quota bar is absent.
        const float top=90,bottom=90;
        // Adjacent slices must meet on device pixels: a fractional seam otherwise
        // leaves an alpha hairline across the panel at 125/150/175 percent DPI.
        var topPixels=(int)Math.Round(top*_scale);var bottomPixels=(int)Math.Round(bottom*_scale);
        g.DrawImage(_panelBase,new Rectangle(0,0,_panel.Width,topPixels),new RectangleF(0,0,_panelBase.Width,top*2),GraphicsUnit.Pixel);
        g.DrawImage(_panelBase,new Rectangle(0,topPixels,_panel.Width,_panel.Height-topPixels-bottomPixels),new RectangleF(0,top*2,_panelBase.Width,_panelBase.Height-(top+bottom)*2),GraphicsUnit.Pixel);
        g.DrawImage(_panelBase,new Rectangle(0,_panel.Height-bottomPixels,_panel.Width,bottomPixels),new RectangleF(0,_panelBase.Height-bottom*2,_panelBase.Width,bottom*2),GraphicsUnit.Pixel);
        g.ScaleTransform((float)_scale,(float)_scale);
        using(var dot=new SolidBrush(_data.Connected?Color.FromArgb(67,209,127):Text3))g.FillEllipse(dot,79,83,6,6);
        Text(g,_data.Connecting?"连接中":_data.Connected?"已连接":"未连接",91,79,55,16,12,Text2,atlas:"status");
        Text(g,_data.Node,149,79,136,16,12,Text2,alignment:StringAlignment.Far);
        Text(g,"实时上传",79,105,85,23,11.5f,Text3,atlas:"label");
        Text(g,key.Upload,165,105,120,23,11.5f,Text1,FontStyle.Bold,StringAlignment.Far,atlas:"bytes");
        Text(g,"实时下载",79,128,85,23,11.5f,Text3,atlas:"label");
        Text(g,key.Download,165,128,120,23,11.5f,Text1,FontStyle.Bold,StringAlignment.Far,atlas:"bytes");
        Text(g,"订阅用量",79,157,68,23,11.5f,Text3,atlas:"label");
        Text(g,key.Quota,145,157,140,23,11.5f,Text1,FontStyle.Bold,StringAlignment.Far,atlas:"bytes");
        var offset=_data.SubscriptionTotal>0?0:-22;
        if(_data.SubscriptionTotal>0)
        {
            FillRounded(g,new(79,188,206,4),2,Color.FromArgb(20,255,255,255));
            if(key.QuotaPixels>0)FillRounded(g,new(79,188,(float)(key.QuotaPixels/_scale),4),2,Accent);
        }
        Text(g,"本次上传",79,202+offset,85,23,11.5f,Text3,atlas:"label");
        Text(g,key.Up,165,202+offset,120,23,11.5f,Text1,FontStyle.Bold,StringAlignment.Far,atlas:"bytes");
        Text(g,"本次下载",79,225+offset,85,23,11.5f,Text3,atlas:"label");
        Text(g,key.Down,165,225+offset,120,23,11.5f,Text1,FontStyle.Bold,StringAlignment.Far,atlas:"bytes");
        var connected=_data.Connected;
        FillRounded(g,new(79,ButtonY,206,32),16,connected?Color.FromArgb(_buttonHover?48:36,255,107,107):Color.FromArgb(_buttonHover?28:18,255,255,255));
        Text(g,_data.Connecting?"连接中":connected?"断开连接":"连接",79,ButtonY,206,32,12,connected?Color.FromArgb(255,138,138):Text1,alignment:StringAlignment.Center,atlas:connected&&!_data.Connecting?"stop":"button");
    }

    private void DrawPanel()
    {
        if(_panelContent==null || _cachedPanel==null)BuildPanel();
        var g=_panel.Begin(1);
        var (factor,x,y)=PanelTransform();
        // Keep subpixel motion throughout the transition, then copy native pixels
        // at rest so settled text does not pass through an extra resampling stage.
        if(_panelValue>=1)g.DrawImageUnscaled(_panelContent!,0,0);
        else g.DrawImage(_panelContent!,new RectangleF(x,y,_panel.Width*factor,_panel.Height*factor));
        _panel.Present((byte)Math.Round(255*Math.Clamp(_opacity,0,1)));
        _panelPresents++;_panelDirty=false;
    }

    private (float Factor,float X,float Y) PanelTransform()
    {
        var factor=(float)(.98+.02*_panelValue);
        return (factor,_panel.Width*(1-factor)/2,(float)(_panel.Height*(1-factor)/2+6*(1-_panelValue)*_scale));
    }
    private void UpdatePanelProgress(double now)
    {
        _panelValue=AnimationsEnabled?_panelFrom+((_panelOpen?1:0)-_panelFrom)*Ease(Math.Clamp((now-_panelAt)/200,0,1)):(_panelOpen?1:0);
        _opacity=AnimationsEnabled?_opacityFrom+((_panelOpen?1:0)-_opacityFrom)*Math.Clamp((now-_panelAt)/160,0,1):(_panelOpen?1:0);
    }

    private void SetPanel(bool open)
    {
        if(_disposed || _panelOpen==open)return;
        var now=Now;
        UpdatePanelProgress(now);
        _panelFrom=_panelValue;_opacityFrom=_opacity; _panelOpen=open; _panelAt=now;
        if(open){PositionPanel();_panelDirty=true;Refresh();_panel.Show();_mouseWasDown=(Native.GetAsyncKeyState(1)&0x8000)!=0;if(!NativeUiReview.Enabled)_input.Start();}
        else{_input.Stop();var captured=_panelPressed;_panelPressed=false;if(captured)Native.ReleaseCapture();}
        if(AnimationsEnabled){_panelAnimating=true;StartFrames();}
        else{_panelValue=_opacity=open?1:0;_panelAt=Now-1000;DrawPanel();if(!open)_panel.Hide();}
    }

    private void PollPanelInput()
    {
        if(_disposed || !_panelOpen){_input.Stop();return;}
        var down=(Native.GetAsyncKeyState(1)&0x8000)!=0;
        if(Native.GetCursorPos(out var p) && down && !_mouseWasDown && !Hit(_ball,p) && !Hit(_panel,p))SetPanel(false);
        if((Native.GetAsyncKeyState(0x1b)&0x8000)!=0)SetPanel(false);
        _mouseWasDown=down;
    }

    private bool Hit(Layer layer,Native.Pt p)
    {
        if(layer.IsPanel)
        {
            var point=PanelPoint(p);
            return _panelOpen && _opacity>.01 && RoundedHit(point.X,point.Y,64,64,236,PanelHeight,18);
        }
        var x=(p.X-layer.X)/_scale;var y=(p.Y-layer.Y)/_scale;
        return (x-80)*(x-80)+(y-80)*(y-80)<=32*32;
    }
    private bool OnButton(Native.Pt p)
    {
        var point=PanelPoint(p);
        return _panelOpen && _opacity>.01 && RoundedHit(point.X,point.Y,79,ButtonY,206,32,16);
    }
    private (double X,double Y) PanelPoint(Native.Pt point)
    {
        var (factor,x,y)=PanelTransform();
        return ((point.X-_panel.X-x)/factor/_scale,(point.Y-_panel.Y-y)/factor/_scale);
    }
    internal static bool RoundedHit(double x,double y,double left,double top,double width,double height,double radius)
    {
        if(x<left || x>left+width || y<top || y>top+height)return false;
        var cx=Math.Clamp(x,left+radius,left+width-radius);var cy=Math.Clamp(y,top+radius,top+height-radius);
        return (x-cx)*(x-cx)+(y-cy)*(y-cy)<=radius*radius;
    }

    private nint Message(Layer layer,uint msg,nint wp,nint lp)
    {
        if(_disposed || _failed)return Native.DefWindowProc(layer.Handle,msg,wp,lp);
        if(msg==0x21)return 3; // MA_NOACTIVATE: showing or dragging never steals focus.
        if(msg==0x84){var point=new Native.Pt((short)(long)lp,(short)((long)lp>>16));return Hit(layer,point)?1:-1;}
        if(msg is 0x31 or 0x318 or 0x317){if(wp!=0)Native.BitBlt(wp,0,0,layer.Width,layer.Height,layer.Dc,0,0,0x00CC0020);return 0;}
        if(msg==0x1a || msg==0x7e || msg==0x2e0){QueueAction(()=>{_animationsEnabled=ReadAnimationsEnabled();Constrain();DrawBall();if(_panelOpen)DrawPanel();if(_ballAnimating || _panelAnimating)Frame();});return 0;}
        if(msg==0x10){QueueAction(()=>Services.FloatingTrafficService.SetEnabled(false));return 0;}
        Native.GetCursorPos(out var p);
        if(msg is 0x200 or 0x201 or 0x202)
        {
            p=new Native.Pt((short)(long)lp,(short)((long)lp>>16));Native.ClientToScreen(layer.Handle,ref p);
        }
        if(layer.IsPanel)
        {
            if(msg==0x200)
            {
                var hover=OnButton(p);
                if(hover!=_buttonHover){_buttonHover=hover;InvalidatePanel();DrawPanel();}
                var tracking=new Native.Track {Size=Marshal.SizeOf<Native.Track>(),Flags=2,Window=layer.Handle};Native.TrackMouseEvent(ref tracking);
            }
            if(msg==0x2a3 && _buttonHover){_buttonHover=false;InvalidatePanel();DrawPanel();return 0;}
            if(msg==0x201){_panelPressed=OnButton(p);if(_panelPressed)Native.SetCapture(layer.Handle);return 0;}
            if(msg==0x202)
            {
                var invoke=_panelPressed && OnButton(p) && !_data.Connecting;
                _panelPressed=false;Native.ReleaseCapture();
                if(invoke)QueueAction(()=>{_toggleConnection();Refresh();});return 0;
            }
            if(msg is 0x215 or 0x1f){_panelPressed=false;return 0;}
        }
        else
        {
            if(msg==0x200)
            {
                if(_dragging)
                {
                    var dx=p.X-_down.X;var dy=p.Y-_down.Y;
                    _moved|=Math.Abs(dx)+Math.Abs(dy)>4*_scale;
                    if(_moved){_click.Stop();_x=_origin.X+dx;_y=_origin.Y+dy;Constrain();DrawBall();if(_panelOpen)DrawPanel();}
                }
                else if(_hoverTo!=1){Hover(true);var tracking=new Native.Track { Size=Marshal.SizeOf<Native.Track>(),Flags=2,Window=layer.Handle };Native.TrackMouseEvent(ref tracking);}
                return 0;
            }
            if(msg==0x2a3){Hover(false);return 0;}
            if(msg==0x201)
            {
                _double=_click.IsRunning && Math.Abs(p.X-_lastClick.X)+Math.Abs(p.Y-_lastClick.Y)<=8*_scale;
                if(_double)_click.Stop();
                _down=new(p.X,p.Y);_origin=new(_x,_y);_moved=false;_dragging=true;Native.SetCapture(layer.Handle);return 0;
            }
            if(msg==0x202 && _dragging)
            {
                _dragging=false;Native.ReleaseCapture();
                if(_moved){_savePosition(_x,_y);_double=false;}
                else if(_double){_double=false;SetPanel(false);QueueAction(_openMain);}
                else{_lastClick=new(p.X,p.Y);SetPanel(!_panelOpen);_click.Start();}
                return 0;
            }
            if(msg is 0x215 or 0x1f){if(_dragging){_dragging=false;_double=false;_click.Stop();_savePosition(_x,_y);}return 0;}
        }
        return Native.DefWindowProc(layer.Handle,msg,wp,lp);
    }

    private void Hover(bool hover)
    {
        if(_hoverTo==(hover?1:0))return;
        _hoverFrom=_hoverValue=_hoverFrom+(_hoverTo-_hoverFrom)*Ease(Math.Clamp((Now-_hoverAt)/180,0,1),.25,.1,.25,1);
        _hoverTo=hover?1:0;_hoverAt=Now;
        if(AnimationsEnabled)StartBallAnimation();else{_hoverValue=_hoverTo;DrawBall();}
    }
    private Font Font(float size,FontStyle style=FontStyle.Regular,bool cjk=false)
    {
        // GDI+'s implicit CJK fallback differs from Chromium/DirectWrite; use the
        // exact explicit fallback in the reference font stack instead.
        var family=cjk?"Microsoft YaHei UI":style==FontStyle.Bold?"Segoe UI Semibold":"Segoe UI Variable Text";
        if(!cjk && style==FontStyle.Bold)style=FontStyle.Regular;
        if(!_fonts.TryGetValue((family,size,style),out var font))_fonts[(family,size,style)]=font=new Font(family,size,style,GraphicsUnit.Pixel);
        return font;
    }
    private void Text(Graphics g,string text,float x,float y,float w,float h,float size,Color colour,FontStyle style=FontStyle.Regular,StringAlignment alignment=StringAlignment.Near,string? atlas=null)
    {
        if(atlas!=null && _sourceText.Draw(g,atlas,text,new(x,y,w,h),alignment,_scale))return;
        _textFormat.Alignment=alignment;using var brush=new SolidBrush(colour);g.DrawString(text,Font(size,style,text.Any(c=>c is >= '\u2e80' and <= '\u9fff')),brush,new RectangleF(x,y,w,h),_textFormat);
    }
    private static void FillRounded(Graphics g,RectangleF r,float radius,Color colour)
    {
        if(r.Width<=0 || r.Height<=0)return;var d=Math.Min(Math.Min(r.Width,r.Height),radius*2);
        using var path=new GraphicsPath();path.AddArc(r.X,r.Y,d,d,180,90);path.AddArc(r.Right-d,r.Y,d,d,270,90);path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.X,r.Bottom-d,d,d,90,90);path.CloseFigure();using var brush=new SolidBrush(colour);g.FillPath(brush,path);
    }
    private static void Configure(Graphics g){g.SmoothingMode=SmoothingMode.AntiAlias;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;}

    internal void SetReviewData(FloatingTrafficData data){if(!NativeUiReview.Enabled)throw new InvalidOperationException();_reviewData=data;Refresh();}
    internal void OpenForReview(bool open){if(!NativeUiReview.Enabled)throw new InvalidOperationException();SetPanel(open);}
    internal void MoveForReview(int x,int y){if(!NativeUiReview.Enabled)throw new InvalidOperationException();_x=x;_y=y;Constrain();DrawBall();if(_panelOpen)DrawPanel();}
    internal object Inspect()=>new {ballHandle=_ball.Handle.ToInt64(),panelHandle=_panel.Handle.ToInt64(),x=_x,y=_y,scale=_scale,panelOpen=_panelOpen,panelProgress=_panelValue,panelOpacity=_panel.Alpha/255d,arcProgress=ArcAt(Now),frameTimerRunning=_frames.IsRunning,sampleTimerRunning=_sample.IsRunning,renderedFrames=_renderedFrames,averageFrameWorkMs=_renderedFrames==0?0:_frameCostMs/_renderedFrames,frameIntervalsMs=_frameIntervals?.ToArray(),ballPresents=_ballPresents,panelPresents=_panelPresents,faceBuilds=_faceBuilds,panelBuilds=_panelBuilds,layoutPasses=_layoutPasses,panelHeight=PanelHeight,uploadRate=Rate(_data.UploadBps),downloadRate=Rate(_data.DownloadBps),fonts=_fonts.Values.Select(f=>f.Name).Distinct().ToArray()};
    internal (long Ball,long Panel,long Faces,long Contents,long Layout) CountersForReview=>(_ballPresents,_panelPresents,_faceBuilds,_panelBuilds,_layoutPasses);
    internal void ScaleForReview(double scale)
    {
        if(!NativeUiReview.Enabled || scale<1 || scale>3)throw new InvalidOperationException();
        _reviewScale=scale;Constrain();Refresh();
    }
    internal (bool Panel,bool Button) HitForReview(double x,double y)
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        var (factor,dx,dy)=PanelTransform();
        var p=new Native.Pt((int)Math.Round(_panel.X+dx+x*_scale*factor),(int)Math.Round(_panel.Y+dy+y*_scale*factor));
        return (Hit(_panel,p),OnButton(p));
    }
    internal void CancelDragForReview()
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        var point=(int)Math.Round(80*_scale);var lp=(nint)((point<<16)|(point&0xffff));
        Message(_ball,0x201,1,lp);Message(_ball,0x1f,0,0);Native.ReleaseCapture();Message(_ball,0x202,0,lp);
    }
    internal bool PanelOpenForReview=>_panelOpen;
    internal void ResetFrameMetricsForReview()
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        _frameIntervals?.Clear();_lastFrameAt=0;_renderedFrames=0;_frameCostMs=0;
    }
    internal void FailNextFrameForReview(){if(!NativeUiReview.Enabled)throw new InvalidOperationException();_failNextFrameForReview=true;_frames.Start();}
    internal void HoverForReview(bool value){if(!NativeUiReview.Enabled)throw new InvalidOperationException();Hover(value);}
    internal bool FrameTimerForReview=>_frames.IsRunning;
    internal bool AnyTimerForReview=>_frames.IsRunning || _sample.IsRunning || _click.IsRunning || _input.IsRunning;
    internal (nint Ball,nint Panel) HandlesForReview=>(_ball.Handle,_panel.Handle);
    internal void ClickForReview()
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        var point=(int)Math.Round(80*_scale);var lp=(nint)((point<<16)|(point&0xffff));
        Message(_ball,0x201,1,lp);Message(_ball,0x202,0,lp);
    }
    internal void DragForReview(int dx,int dy)
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        var point=(int)Math.Round(80*_scale);var start=(nint)((point<<16)|(point&0xffff));
        Message(_ball,0x201,1,start);
        Message(_ball,0x200,1,(nint)(((point+dy)<<16)|((point+dx)&0xffff)));
        Message(_ball,0x202,0,start);
    }
    internal void CaptureForReview(string folder,string name)
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        _ball.Bitmap!.Save(Path.Combine(folder,name+"-ball.png"),ImageFormat.Png);
        if(_panelContent!=null)
        {
            // The layer's constant opacity is applied by UpdateLayeredWindow, not
            // stored in its DIB. Include it in review frames to show real fades.
            using var frame=new Bitmap(_panel.Width,_panel.Height,PixelFormat.Format32bppPArgb);
            using var g=Graphics.FromImage(frame);
            using var opacity=new ImageAttributes();opacity.SetColorMatrix(new ColorMatrix {Matrix33=_panel.Alpha/255f});
            g.DrawImage(_panel.Bitmap!,new Rectangle(0,0,_panel.Width,_panel.Height),0,0,_panel.Width,_panel.Height,GraphicsUnit.Pixel,opacity);
            frame.Save(Path.Combine(folder,name+"-panel.png"),ImageFormat.Png);
        }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        _sample.Stop();_frames.Stop();_input.Stop();_click.Stop();
        if(_dragging || _panelPressed)Native.ReleaseCapture();
        _panel.Dispose();_ball.Dispose();ReleaseFaces();_panelContent?.Dispose();_panelBase.Dispose();_normal.Dispose();_hover.Dispose();
        foreach(var font in _fonts.Values)font.Dispose();_fonts.Clear();_textFormat.Dispose();_sourceText.Dispose();
    }

    private sealed class Layer : IDisposable
    {
        private static readonly Dictionary<nint,Layer> Instances=new();
        private static readonly Native.Proc Procedure=Dispatch;
        private static ushort _class;
        private readonly FloatingTrafficWindow _owner;
        internal readonly bool IsPanel;
        internal nint Handle { get; private set; }
        internal nint Dc { get; private set; }
        private nint _dib,_old;
        internal Bitmap? Bitmap { get;private set; }
        private Graphics? _graphics;
        internal int X,Y,Width,Height;
        internal byte Alpha {get;private set;}=255;
        private bool _positioned;
        private static nint _host;
        internal Layer(FloatingTrafficWindow owner,bool panel)
        {
            _owner=owner;IsPanel=panel;
            if(_class==0){var cls=new Native.Class { Size=(uint)Marshal.SizeOf<Native.Class>(),Instance=Native.GetModuleHandle(null),Procedure=Procedure,Name="NeoFloatingTraffic",Cursor=Native.LoadCursor(0,32512) };_class=Native.RegisterClassEx(ref cls);if(_class==0)throw new Win32Exception();}
            if(_host==0)_host=Native.CreateWindowEx(0x08000080,"NeoFloatingTraffic","neo 悬浮球宿主",0x80000000,0,0,1,1,0,0,Native.GetModuleHandle(null),0);
            Handle=Native.CreateWindowEx(0x08080080,"NeoFloatingTraffic",panel?"neo 流量详情":"neo 流量悬浮球",0x80000000,0,0,1,1,_host,0,Native.GetModuleHandle(null),0);
            if(Handle==0)throw new Win32Exception();Instances[Handle]=this;
        }
        private static nint Dispatch(nint h,uint msg,nint wp,nint lp)
        {
            try{return Instances.TryGetValue(h,out var layer)?layer._owner.Message(layer,msg,wp,lp):Native.DefWindowProc(h,msg,wp,lp);}
            catch(Exception ex){if(Instances.TryGetValue(h,out var layer))layer._owner.Fail(ex);return 0;}
        }
        internal void Resize(int w,int h)
        {
            if(Width==w && Height==h)return;ReleaseSurface();Width=w;Height=h;
            Dc=Native.CreateCompatibleDC(0);var header=new Native.BitmapHeader { Size=40,Width=w,Height=-h,Planes=1,Bits=32 };
            _dib=Native.CreateDIBSection(Dc,ref header,0,out var pixels,0,0);if(_dib==0)throw new Win32Exception();
            _old=Native.SelectObject(Dc,_dib);Bitmap=new Bitmap(w,h,w*4,PixelFormat.Format32bppPArgb,pixels);_graphics=Graphics.FromImage(Bitmap);Configure(_graphics);
        }
        internal Graphics Begin(double scale)
        {
            var g=_graphics!;g.ResetTransform();g.CompositingMode=CompositingMode.SourceCopy;g.Clear(Color.Transparent);g.CompositingMode=CompositingMode.SourceOver;g.ScaleTransform((float)scale,(float)scale);return g;
        }
        internal void Move(int x,int y){if(_positioned && X==x && Y==y)return;_positioned=true;X=x;Y=y;Native.SetWindowPos(Handle,NativeUiReview.Enabled?new nint(1):new nint(-1),x,y,0,0,0x11);}
        internal void Show()=>Native.ShowWindow(Handle,4);
        internal void Hide()=>Native.ShowWindow(Handle,0);
        internal void Present(byte alpha=255)
        {
            Alpha=alpha;
            _graphics!.Flush(FlushIntention.Sync);
            var dst=new Native.Pt(X,Y);var src=new Native.Pt(0,0);var size=new Native.Size(Width,Height);var blend=new Native.Blend { Alpha=alpha,Format=1 };
            if(!Native.UpdateLayeredWindow(Handle,0,ref dst,ref size,Dc,ref src,0,ref blend,2))throw new Win32Exception();
        }
        private void ReleaseSurface(){_graphics?.Dispose();_graphics=null;Bitmap?.Dispose();Bitmap=null;if(_old!=0)Native.SelectObject(Dc,_old);if(_dib!=0)Native.DeleteObject(_dib);if(Dc!=0)Native.DeleteDC(Dc);_old=_dib=0;Dc=0;}
        public void Dispose(){if(Handle!=0){Native.DestroyWindow(Handle);Instances.Remove(Handle);Handle=0;}ReleaseSurface();if(Instances.Count==0 && _host!=0){Native.DestroyWindow(_host);_host=0;}}
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]internal struct Pt(int x,int y){internal int X=x,Y=y;}
        [StructLayout(LayoutKind.Sequential)]internal struct Size(int w,int h){internal int Width=w,Height=h;}
        [StructLayout(LayoutKind.Sequential)]internal struct Rect{internal int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)]internal struct Monitor{internal int Size;internal Rect Bounds,Work;internal uint Flags;}
        [StructLayout(LayoutKind.Sequential,Pack=1)]internal struct Blend{internal byte Op,Flags,Alpha,Format;}
        [StructLayout(LayoutKind.Sequential)]internal struct BitmapHeader{internal uint Size;internal int Width,Height;internal ushort Planes,Bits;internal uint Compression,ImageSize;internal int XPixels,YPixels;internal uint Colors,Important;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct Class{internal uint Size,Style;internal Proc Procedure;internal int Extra,WindowExtra;internal nint Instance,Icon,Cursor,Background;internal string? Menu;internal string Name;internal nint SmallIcon;}
        [StructLayout(LayoutKind.Sequential)]internal struct Track{internal int Size;internal uint Flags;internal nint Window;internal uint HoverTime;}
        internal delegate nint Proc(nint h,uint msg,nint w,nint l);
        internal static Rect Work(nint monitor){var info=new Monitor { Size=Marshal.SizeOf<Monitor>() };if(!GetMonitorInfo(monitor,ref info))throw new Win32Exception();return info.Work;}
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern ushort RegisterClassEx(ref Class cls);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern nint DefWindowProc(nint h,uint msg,nint w,nint l);
        [DllImport("user32.dll")]internal static extern bool DestroyWindow(nint h);
        [DllImport("user32.dll")]internal static extern bool ShowWindow(nint h,int command);
        [DllImport("user32.dll")]internal static extern bool SetWindowPos(nint h,nint after,int x,int y,int w,int height,uint flags);
        [DllImport("user32.dll",SetLastError=true)]internal static extern bool UpdateLayeredWindow(nint h,nint dst,ref Pt point,ref Size size,nint source,ref Pt origin,uint key,ref Blend blend,uint flags);
        [DllImport("gdi32.dll")]internal static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll",SetLastError=true)]internal static extern nint CreateDIBSection(nint dc,ref BitmapHeader header,uint usage,out nint pixels,nint section,uint offset);
        [DllImport("gdi32.dll")]internal static extern nint SelectObject(nint dc,nint obj);
        [DllImport("gdi32.dll")]internal static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll")]internal static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")]internal static extern bool BitBlt(nint dst,int x,int y,int w,int h,nint src,int sx,int sy,uint rop);
        [DllImport("user32.dll")]internal static extern nint MonitorFromPoint(Pt point,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool GetMonitorInfo(nint monitor,ref Monitor info);
        [DllImport("shcore.dll")]internal static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
        [DllImport("user32.dll")]internal static extern bool GetCursorPos(out Pt p);
        [DllImport("user32.dll")]internal static extern bool ClientToScreen(nint h,ref Pt p);
        [DllImport("user32.dll")]internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]internal static extern nint SetCapture(nint h);
        [DllImport("user32.dll")]internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")]internal static extern bool TrackMouseEvent(ref Track tracking);
        [DllImport("user32.dll")]internal static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")]internal static extern nint LoadCursor(nint instance,int id);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool SystemParametersInfo(uint action,uint param,out bool result,uint flags);
    }
}
