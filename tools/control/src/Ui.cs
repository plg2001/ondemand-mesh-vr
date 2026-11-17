// Look of the panel: colours, fonts, scaling with the screen's DPI and the few hand-drawn controls (the rail of pages
// with their lamps, buttons, console tabs, sections, the mark).
//
// The panel is meant to read like the control cabinet of a lab bench, not like a web page: light grey (close to RAL 7035,
// the grey of electrical cabinets) for the fixed parts, white for the page in use, graphite ink, DIN lettering
// (Bahnschrift, in every Windows 10/11) for names and Segoe UI for the forms. Colour is only used for meaning, with the
// lamp colours of IEC 60073: green = running / starts something, red = fault / stops something, amber = in between (it
// blinks while something starts), white = information, grey = off. No icons and no boxes around everything: buttons say
// what they do, sections are a title and their controls.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EdgeControl;

static class Theme
{
    public static float Scale = 1f;
    public static int S(int px) => (int)Math.Round(px * Scale);
    public static Padding P(int all) => new Padding(S(all));
    public static Padding P(int left, int top, int right, int bottom) => new Padding(S(left), S(top), S(right), S(bottom));

    // surfaces
    public static readonly Color Cabinet = Color.FromArgb(220, 222, 217);       // rail, action strip
    public static readonly Color CabinetLight = Color.FromArgb(230, 232, 227);  // rail entry under the mouse
    public static readonly Color Sheet = Color.White;                           // the page in use
    public static readonly Color Rule = Color.FromArgb(178, 182, 176);          // edges on the cabinet grey
    public static readonly Color RuleSoft = Color.FromArgb(206, 210, 204);      // edges on the white sheet

    // ink
    public static readonly Color Ink = Color.FromArgb(39, 44, 46);
    public static readonly Color Muted = Color.FromArgb(90, 97, 96);
    public static readonly Color Faint = Color.FromArgb(150, 156, 150);         // disabled

    // paint of the buttons (white text on them) and text that carries a state
    public static readonly Color Go = Color.FromArgb(30, 122, 70);
    public static readonly Color Stop = Color.FromArgb(180, 35, 24);
    public static readonly Color AmberInk = Color.FromArgb(138, 90, 0);

    // lamps: brighter, they are lights
    public static readonly Color LampGo = Color.FromArgb(46, 181, 93);
    public static readonly Color LampStop = Color.FromArgb(224, 53, 43);
    public static readonly Color LampAmber = Color.FromArgb(240, 168, 24);
    public static readonly Color LampInfo = Color.FromArgb(240, 242, 237);

    // console
    public static readonly Color ConsoleBg = Color.FromArgb(42, 46, 48);
    public static readonly Color ConsoleBar = Color.FromArgb(31, 35, 37);
    public static readonly Color ConsoleText = Color.FromArgb(205, 209, 203);
    public static readonly Color ConsoleBright = Color.FromArgb(244, 245, 241);
    public static readonly Color ConsoleDim = Color.FromArgb(140, 147, 142);
    public static readonly Color ConsoleGood = Color.FromArgb(132, 200, 150);
    public static readonly Color ConsoleBad = Color.FromArgb(238, 139, 127);
    public static readonly Color ConsoleWarn = Color.FromArgb(227, 192, 101);

    public static Font Ui, Strong, Small, Heading, Name, Mono, MonoBold;

    /// <summary>Lamps may blink: Windows' "Animation effects" are on.</summary>
    public static bool Blinks = true;

    public static void Init(float dpi)
    {
        Scale = Math.Max(1f, dpi / 96f);
        HashSet<string> families;
        using (var installed = new InstalledFontCollection())
            families = new HashSet<string>(installed.Families.Select(f => f.Name));
        bool din = families.Contains("Bahnschrift SemiBold"), semibold = families.Contains("Segoe UI Semibold");
        Ui = new Font("Segoe UI", 9f);
        Strong = semibold ? new Font("Segoe UI Semibold", 9f) : new Font("Segoe UI", 9f, FontStyle.Bold);
        Small = new Font("Segoe UI", 8.25f);
        Heading = din ? new Font("Bahnschrift SemiBold", 10.5f) : new Font("Segoe UI", 10f, FontStyle.Bold);
        Name = din ? new Font("Bahnschrift SemiBold", 11.5f) : new Font("Segoe UI", 11f, FontStyle.Bold);
        Mono = new Font(families.Contains("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9f);
        MonoBold = new Font(Mono, FontStyle.Bold);
        Blinks = Native.AnimationsEnabled();
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color BackOf(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Sheet;
    }

    /// <summary>Height of a line of text in a font.</summary>
    public static int LineHeight(Font font) => TextRenderer.MeasureText("Ag", font, Size.Empty, TextFormatFlags.NoPadding).Height;
}

/// <summary>An indicator lamp: a disc in its colour with a darker rim, or grey when off (Color.Empty).</summary>
static class Lamp
{
    public static void Draw(Graphics g, Rectangle r, Color color)
    {
        bool lit = color != Color.Empty;
        var fill = lit ? color : Color.FromArgb(176, 180, 174);
        var rim = lit ? Color.FromArgb((int)(color.R * 0.7), (int)(color.G * 0.7), (int)(color.B * 0.7)) : Color.FromArgb(138, 143, 137);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, r);
        using (var pen = new Pen(rim, Math.Max(1f, Theme.Scale))) g.DrawEllipse(pen, r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        g.SmoothingMode = mode;
    }
}

/// <summary>Start = green, it starts something (a server, a session, a script); Stop = red, it stops it; Plain = the rest;
/// Link = underlined text, it opens something.</summary>
enum Look { Start, Stop, Plain, Link }

/// <summary>A button that says what it does, painted in the colour of what it does.</summary>
class FlatButton : Button
{
    Look look;
    bool hover, pressed;
    Font underlined;

    public FlatButton(string text, Look look = Look.Plain)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        this.look = look;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = FontOf(look);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, Theme.S(look == Look.Link ? 14 : 6), Theme.S(6));
        Text = text;
        AccessibleName = text;
        Size = GetPreferredSize(Size.Empty);
    }

    static Font FontOf(Look look) => look == Look.Start || look == Look.Stop ? Theme.Strong : Theme.Ui;

    public Look Look
    {
        get => look;
        set
        {
            look = value;
            Font = FontOf(look);
            underlined = null;
            Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(text.Width + Theme.S(look == Look.Link ? 2 : 26), Math.Max(Theme.S(28), text.Height + Theme.S(10)));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var parent = Theme.BackOf(this);
        g.Clear(parent);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (look == Look.Link)
        {
            underlined ??= new Font(Font, FontStyle.Underline);
            var ink = !Enabled ? Theme.Faint : hover || pressed ? Color.Black : Theme.Ink;
            TextRenderer.DrawText(g, Text, Enabled ? underlined : Font, ClientRectangle, ink, parent, flags);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle, ink, parent);
            return;
        }
        Color back, fore, border;
        switch (look)
        {
            case Look.Start:
                back = pressed ? Color.FromArgb(18, 90, 50) : hover ? Color.FromArgb(24, 106, 60) : Theme.Go;
                fore = Color.White;
                border = back;
                break;
            case Look.Stop:
                back = pressed ? Color.FromArgb(132, 24, 16) : hover ? Color.FromArgb(156, 29, 20) : Theme.Stop;
                fore = Color.White;
                border = back;
                break;
            default:
                back = pressed ? Color.FromArgb(226, 228, 223) : hover ? Color.FromArgb(242, 243, 240) : Color.White;
                fore = Theme.Ink;
                border = hover ? Color.FromArgb(140, 146, 139) : Color.FromArgb(167, 172, 166);
                break;
        }
        if (!Enabled)
        {
            back = Color.FromArgb(232, 234, 230);
            fore = Theme.Faint;
            border = Color.FromArgb(214, 217, 212);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.S(2)))
        using (var brush = new SolidBrush(back))
        using (var pen = new Pen(border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = SmoothingMode.Default;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.S(5), 0, Width - Theme.S(10), Height), fore, back, flags);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -Theme.S(3), -Theme.S(3)), fore, back);
    }
}

/// <summary>"▸ Opzioni avanzate": shows or hides a part of the page.</summary>
sealed class Disclosure : Button
{
    bool open, hover;

    public Disclosure(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Ui;
        Cursor = Cursors.Hand;
        Text = text;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.CheckButton;
        Margin = new Padding(0, 0, Theme.S(14), 0);
        var size = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding);
        Size = new Size(size.Width + Theme.S(18), Math.Max(Theme.S(26), size.Height + Theme.S(8)));
    }

    public bool Open
    {
        get => open;
        set
        {
            open = value;
            AccessibleDescription = open ? "aperte" : "chiuse";
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Theme.BackOf(this);
        g.Clear(back);
        var ink = hover ? Color.Black : Theme.Ink;
        float cy = Height / 2f, x = Theme.S(1), a = Theme.S(4);
        var triangle = open
            ? new[] { new PointF(x, cy - a * 0.55f), new PointF(x + 2 * a, cy - a * 0.55f), new PointF(x + a, cy + a * 0.65f) }
            : new[] { new PointF(x + a * 0.4f, cy - a), new PointF(x + a * 0.4f, cy + a), new PointF(x + a * 1.5f, cy) };
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(ink)) g.FillPolygon(brush, triangle);
        g.SmoothingMode = SmoothingMode.Default;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.S(14), 0, Width - Theme.S(14), Height), ink, back,
                              TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle, ink, back);
    }
}

/// <summary>A page in the rail on the left: its name, its lamp and a few lines of live state. The selected one is white
/// and joins the page, like the tab of a binder.</summary>
sealed class RailEntry : Button
{
    readonly bool hasLamp;
    string[] lines = new string[0];
    Color lamp = Color.Empty;
    bool blinking, hover, selected;

    /// <summary>The lit half of the blinking cycle (flipped by the window's timer).</summary>
    public static bool BlinkLit = true;

    public RailEntry(string name, int lineCount, bool hasLamp)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        this.hasLamp = hasLamp;
        Text = name;
        AccessibleName = name;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Margin = Padding.Empty;
        Height = Theme.S(11) + Theme.LineHeight(Theme.Name) + Theme.S(3) + lineCount * StateLine + Theme.S(12);
    }

    static int StateLine => Theme.LineHeight(Theme.Small) + Theme.S(1);

    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            Invalidate();
        }
    }

    public bool Blinking => blinking;

    /// <summary>All the state lines, for the tooltip.</summary>
    public string StateText => string.Join("\n", lines);

    /// <summary>The lamp (Color.Empty = off), whether it blinks, the state lines (empty ones are left out); false if
    /// nothing changed.</summary>
    public bool Set(Color lampColor, bool blink, params string[] state)
    {
        var shown = state.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        if (lampColor == lamp && blink == blinking && shown.SequenceEqual(lines)) return false;
        lamp = lampColor;
        blinking = blink;
        lines = shown;
        AccessibleDescription = string.Join(", ", lines);
        Invalidate();
        return true;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = selected ? Theme.Sheet : hover ? Theme.CabinetLight : Theme.Cabinet;
        g.Clear(back);
        if (!selected)
            using (var pen = new Pen(Theme.Rule))
                g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        int x = Theme.S(33), y = Theme.S(11), nameHeight = Theme.LineHeight(Theme.Name), line = StateLine;
        if (hasLamp)
        {
            int d = Theme.S(10);
            Lamp.Draw(g, new Rectangle(Theme.S(14), y + (nameHeight - d) / 2 + Theme.S(1), d, d),
                      blinking && !BlinkLit ? Color.Empty : lamp);
        }
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, Text, Theme.Name, new Rectangle(x, y, Width - x - Theme.S(8), nameHeight), Theme.Ink, back, flags);
        y += nameHeight + Theme.S(3);
        foreach (var text in lines)
        {
            if (y + line > Height) break;
            TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(x, y, Width - x - Theme.S(10), line), Theme.Muted, back, flags);
            y += line;
        }
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -Theme.S(3), -Theme.S(3)));
    }
}

/// <summary>A tab of the console bar; the selected one has the console's colour, as if it were part of it.</summary>
sealed class TabButton : Button
{
    bool hover, selected;
    Color dot = Color.Empty;

    public TabButton(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Text = text;
        AccessibleName = text;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Ui;
        Cursor = Cursors.Hand;
        Margin = Padding.Empty;
        Size = new Size(TextRenderer.MeasureText(text, Theme.Strong).Width + Theme.S(38), Theme.S(30));
    }

    public bool Selected
    {
        get => selected;
        set { selected = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Invalidate();
    }

    /// <summary>The tab's lamp (Color.Empty = none).</summary>
    public Color Dot
    {
        get => dot;
        set { if (value != dot) { dot = value; Invalidate(); } }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = selected ? Theme.ConsoleBg : hover ? Color.FromArgb(38, 43, 45) : Theme.ConsoleBar;
        g.Clear(back);
        int d = Theme.S(7), x = Theme.S(11);
        if (dot != Color.Empty) Lamp.Draw(g, new Rectangle(x, (Height - d) / 2, d, d), dot);
        x += Theme.S(14);
        TextRenderer.DrawText(g, Text, selected ? Theme.Strong : Theme.Ui, new Rectangle(x, 0, Width - x, Height),
                              selected ? Theme.ConsoleBright : Theme.ConsoleDim, back,
                              TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -Theme.S(2), -Theme.S(3)), Theme.ConsoleText, back);
    }
}

/// <summary>A titled part of a page: the title in DIN lettering and the controls under it, with no box around them.</summary>
class Section : Panel
{
    public readonly string Title;

    public Section(string title = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Title = title;
        BackColor = Theme.Sheet;
        Padding = title == null ? Padding.Empty : new Padding(0, Theme.LineHeight(Theme.Heading) + Theme.S(8), 0, 0);
        Margin = Theme.P(0, 0, 28, 12);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (Title != null)
            TextRenderer.DrawText(e.Graphics, Title, Theme.Heading, Point.Empty, Theme.Ink, BackColor, TextFormatFlags.NoPadding);
    }
}

/// <summary>The grey strip that holds a page's main actions, in the cabinet colour like the rail.</summary>
sealed class Band : Panel
{
    public Band()
    {
        BackColor = Theme.Cabinet;
        Padding = Theme.P(12, 10, 12, 4);
        Margin = Theme.P(0, 2, 0, 10);
    }
}

/// <summary>The app's mark, the same drawing as EdgeControl.ico: on a grey plate, a patch of a triangle mesh rebuilt from
/// the right while the points on the left still wait, which is what the edge server does.</summary>
static class Logo
{
    public static void Draw(Graphics g, Rectangle r)
    {
        float s = r.Width;
        PointF At(double u, double v) => new PointF(r.X + (float)(u * s), r.Y + (float)(v * s));
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float line = Math.Max(1f, (float)Math.Round(s / 48f));
        var plate = Color.FromArgb(233, 235, 230);
        using (var path = Theme.Round(new RectangleF(r.X + line / 2, r.Y + line / 2, s - line, s - line), Math.Max(1.5f, s * 0.08f)))
        using (var brush = new SolidBrush(plate))
        using (var pen = new Pen(Color.FromArgb(112, 118, 111), line))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        // six facets around one vertex, a little irregular like a mesh rebuilt from a scan, lit from the top left
        var centre = At(.61, .52);
        var ring = new[] { At(.37, .49), At(.49, .26), At(.74, .28), At(.86, .50), At(.74, .74), At(.50, .75) };
        var shades = new[]
        {
            Color.FromArgb(104, 184, 130), Color.FromArgb(80, 166, 112), Color.FromArgb(58, 148, 94),
            Color.FromArgb(20, 94, 53), Color.FromArgb(28, 116, 66), Color.FromArgb(40, 134, 80),
        };
        for (int i = 0; i < 6; i++)
        {
            var facet = new[] { centre, ring[i], ring[(i + 1) % 6] };
            using (var brush = new SolidBrush(shades[i])) g.FillPolygon(brush, facet);
            if (s >= 30)  // the edges between facets; below that they would only blur them
                using (var pen = new Pen(plate, line) { LineJoin = LineJoin.Round })
                    g.DrawPolygon(pen, facet);
        }
        // the points still waiting on the left, where the next facets will be
        var loose = new[] { At(.25, .29), At(.15, .52), At(.26, .73) };
        float dot = Math.Max(1.7f, s * 0.075f);
        using (var brush = new SolidBrush(Theme.Ink))
            foreach (var p in loose)
                g.FillEllipse(brush, p.X - dot / 2, p.Y - dot / 2, dot, dot);
        g.SmoothingMode = mode;
    }

    /// <summary>EdgeControl.exe --icon FILE: the mark as a Windows icon (PNG images, 16 to 256 pixels).</summary>
    public static void WriteIcon(string path)
    {
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = sizes.Select(size =>
        {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                Draw(g, new Rectangle(0, 0, size, size));
            }
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return png.ToArray();
        }).ToList();
        using var file = new BinaryWriter(File.Create(path));
        file.Write((short)0);
        file.Write((short)1);
        file.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            file.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            file.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            file.Write((byte)0);
            file.Write((byte)0);
            file.Write((short)1);
            file.Write((short)32);
            file.Write(images[i].Length);
            file.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) file.Write(image);
    }
}

/// <summary>A ListView that does not flicker while its rows change.</summary>
sealed class SmoothListView : ListView
{
    public SmoothListView()
    {
        DoubleBuffered = true;
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        Font = Theme.Ui;
    }
}

/// <summary>Small factories, so that the pages read as a description of their layout.</summary>
static class Make
{
    public static Label Label(string text, Font font = null, Color? color = null, int maxWidth = 0) => new Label
    {
        Text = text, AutoSize = true, Font = font ?? Theme.Ui, ForeColor = color ?? Theme.Ink,
        BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, Theme.S(4)),
        MaximumSize = maxWidth > 0 ? new Size(Theme.S(maxWidth), 0) : Size.Empty, UseMnemonic = false,
    };

    public static RadioButton Radio(string text) => new RadioButton
    {
        Text = text, AutoSize = true, Font = Theme.Ui, ForeColor = Theme.Ink,
        Margin = new Padding(0, Theme.S(2), Theme.S(8), Theme.S(1)), UseMnemonic = false, Cursor = Cursors.Hand,
    };

    public static CheckBox Check(string text, bool value = false) => new CheckBox
    {
        Text = text, AutoSize = true, Checked = value, Font = Theme.Ui, ForeColor = Theme.Ink,
        Margin = new Padding(0, Theme.S(4), Theme.S(14), Theme.S(2)), UseMnemonic = false, Cursor = Cursors.Hand,
    };

    public static ComboBox Combo(int width, params object[] items)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.S(width), Font = Theme.Ui,
            Margin = new Padding(0, Theme.S(1), Theme.S(8), Theme.S(3)), FlatStyle = FlatStyle.System,
        };
        combo.Items.AddRange(items);
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }

    public static NumericUpDown Number(decimal min, decimal max, decimal value, int width = 64, int decimals = 0) => new NumericUpDown
    {
        Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), DecimalPlaces = decimals,
        Width = Theme.S(width), Font = Theme.Ui, Margin = new Padding(0, Theme.S(1), Theme.S(6), Theme.S(3)),
        TextAlign = HorizontalAlignment.Right,
    };

    public static TextBox Text(int width, string value = "", string cue = null)
    {
        var box = new TextBox { Width = Theme.S(width), Text = value, Font = Theme.Ui, Margin = new Padding(0, Theme.S(1), Theme.S(8), Theme.S(3)) };
        if (cue != null) box.HandleCreated += (s, e) => Native.SendMessage(box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, cue);
        return box;
    }

    /// <summary>Controls side by side.</summary>
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        row.Controls.AddRange(controls.Where(c => c != null).ToArray());
        return row;
    }

    /// <summary>Controls one under the other.</summary>
    public static FlowLayoutPanel Stack(params Control[] controls)
    {
        var stack = Row(controls);
        stack.FlowDirection = FlowDirection.TopDown;
        return stack;
    }

    /// <summary>Text vertically centred with the controls of a row.</summary>
    public static Label Inline(string text, Color? color = null)
    {
        var label = Label(text, Theme.Ui, color ?? Theme.Ink);
        label.Margin = new Padding(0, Theme.S(5), Theme.S(6), 0);
        return label;
    }

    /// <summary>A section whose height follows its content (a stack inside); no title = just the controls.</summary>
    public static Section Section(string title, params Control[] content) => Holding(new Section(title), content);

    /// <summary>The grey strip of a page's main actions.</summary>
    public static Band Band(params Control[] content) => Holding(new Band(), content);

    static T Holding<T>(T panel, Control[] content) where T : Panel
    {
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Dock = DockStyle.Fill;
        var stack = Stack(content);
        stack.Location = new Point(panel.Padding.Left, panel.Padding.Top);
        panel.Controls.Add(stack);
        return panel;
    }

    /// <summary>A control in a thin frame (lists, previews); a given size instead of filling the parent.</summary>
    public static Panel Framed(Control inner, Size? size = null)
    {
        var frame = new Panel { BackColor = Theme.RuleSoft, Padding = new Padding(1), Margin = Padding.Empty };
        if (size.HasValue) frame.Size = new Size(size.Value.Width + 2, size.Value.Height + 2);
        else frame.Dock = DockStyle.Fill;
        inner.Dock = DockStyle.Fill;
        frame.Controls.Add(inner);
        return frame;
    }

    /// <summary>A one-column table: rows sized by their content, except the ones given a percentage.</summary>
    public static TableLayoutPanel Column(params (Control control, float percent)[] rows)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, BackColor = Color.Transparent, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows.Length; i++)
        {
            table.RowStyles.Add(rows[i].percent > 0 ? new RowStyle(SizeType.Percent, rows[i].percent) : new RowStyle(SizeType.AutoSize));
            if (rows[i].control != null)
            {
                rows[i].control.Dock = rows[i].percent > 0 || rows[i].control is TableLayoutPanel || rows[i].control is Section ||
                                       rows[i].control is Band
                    ? DockStyle.Fill : rows[i].control.Dock;
                table.Controls.Add(rows[i].control, 0, i);
            }
        }
        return table;
    }

    /// <summary>A one-row table with columns of the given percentages (0 = sized by content).</summary>
    public static TableLayoutPanel Columns(params (Control control, float percent)[] columns)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 1, ColumnCount = columns.Length, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = Padding.Empty,
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < columns.Length; i++)
        {
            table.ColumnStyles.Add(columns[i].percent > 0 ? new ColumnStyle(SizeType.Percent, columns[i].percent) : new ColumnStyle(SizeType.AutoSize));
            if (columns[i].control != null)
            {
                columns[i].control.Dock = DockStyle.Fill;
                table.Controls.Add(columns[i].control, i, 0);
            }
        }
        return table;
    }
}

/// <summary>A page of the panel (one entry of the rail).</summary>
abstract class Page : Panel
{
    protected readonly MainForm App;
    public abstract string NavText { get; }

    protected Page(MainForm app)
    {
        App = app;
        Dock = DockStyle.Fill;
        BackColor = Theme.Sheet;
        Padding = Theme.P(22, 16, 12, 6);
        AutoScroll = true;
        Visible = false;
    }

    /// <summary>Adds the page's layout; below this size (unscaled pixels) the page scrolls instead of squeezing it.</summary>
    protected void Install(TableLayoutPanel root, int minWidth, int minHeight)
    {
        root.MinimumSize = new Size(Theme.S(minWidth), Theme.S(minHeight));
        AutoScrollMinSize = root.MinimumSize;
        Controls.Add(root);
    }

    public virtual void Showing() { }

    /// <summary>Server, headset, PC or tasks changed: enable / disable, refresh the texts.</summary>
    public virtual void StatusChanged() { }
}
