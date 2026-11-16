// Look of the panel: colours, fonts, scaling with the screen's DPI and the few hand-drawn controls (buttons,
// navigation, cards, status cards).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace EdgeControl;

static class Theme
{
    public static float Scale = 1f;
    public static int S(int px) => (int)Math.Round(px * Scale);
    public static Padding P(int all) => new Padding(S(all));
    public static Padding P(int left, int top, int right, int bottom) => new Padding(S(left), S(top), S(right), S(bottom));

    public static readonly Color Bg = Color.FromArgb(241, 243, 246);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(220, 224, 230);
    public static readonly Color Text = Color.FromArgb(17, 24, 39);
    public static readonly Color Muted = Color.FromArgb(100, 108, 122);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentDark = Color.FromArgb(29, 78, 216);
    public static readonly Color Green = Color.FromArgb(22, 163, 74);
    public static readonly Color Amber = Color.FromArgb(217, 119, 6);
    public static readonly Color Red = Color.FromArgb(220, 38, 38);
    public static readonly Color RedDark = Color.FromArgb(185, 28, 28);
    public static readonly Color Gray = Color.FromArgb(160, 166, 176);
    public static readonly Color Nav = Color.FromArgb(30, 41, 59);
    public static readonly Color NavHover = Color.FromArgb(44, 57, 78);
    public static readonly Color NavSelected = Color.FromArgb(51, 65, 85);
    public static readonly Color ConsoleBg = Color.FromArgb(24, 24, 27);
    public static readonly Color ConsoleBar = Color.FromArgb(39, 39, 42);
    public static readonly Color ConsoleText = Color.FromArgb(212, 212, 216);

    public static Font Ui, Bold, Small, SmallBold, Title, Subtitle, Mono, Icon, IconBig;
    public static bool HasIcons;

    public static void Init(float dpi)
    {
        Scale = Math.Max(1f, dpi / 96f);
        Ui = new Font("Segoe UI", 9.75f);
        Bold = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        Small = new Font("Segoe UI", 8.5f);
        SmallBold = new Font("Segoe UI", 8.25f, FontStyle.Bold);
        Title = new Font("Segoe UI", 13.5f, FontStyle.Bold);
        Subtitle = new Font("Segoe UI", 11f, FontStyle.Bold);
        Mono = new Font(new InstalledFontCollection().Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9.5f);
        HasIcons = new InstalledFontCollection().Families.Any(f => f.Name == "Segoe MDL2 Assets");
        Icon = new Font(HasIcons ? "Segoe MDL2 Assets" : "Segoe UI Symbol", 10.5f);
        IconBig = new Font(HasIcons ? "Segoe MDL2 Assets" : "Segoe UI Symbol", 13f);
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
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
        return Bg;
    }
}

/// <summary>Icons of the Segoe MDL2 Assets font (Windows 10/11).</summary>
static class Glyph
{
    public const string Server = "", Headset = "", Results = "", Tools = "", Play = "",
                        Stop = "", Record = "", Refresh = "", Folder = "", Copy = "",
                        Save = "", Delete = "", Download = "", Wifi = "", Usb = "",
                        Web = "", Document = "", Terminal = "", Check = "", Send = "",
                        Power = "", Repair = "", Link = "", Upload = "", Log = "",
                        Settings = "";
}

enum Look { Primary, Secondary, Danger, Ghost }

/// <summary>A flat button with rounded corners and an optional icon.</summary>
class FlatButton : Button
{
    Look look;
    string glyph;
    bool hover, pressed;

    public FlatButton(string text, Look look = Look.Secondary, string glyph = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        this.look = look;
        this.glyph = glyph;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = look == Look.Primary || look == Look.Danger ? Theme.Bold : Theme.Ui;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, Theme.S(8), Theme.S(6));
        Text = text;
        AccessibleName = text;
        Size = GetPreferredSize(Size.Empty);
    }

    public Look Look
    {
        get => look;
        set { look = value; Font = look == Look.Primary || look == Look.Danger ? Theme.Bold : Theme.Ui; Invalidate(); }
    }

    public string Icon
    {
        get => glyph;
        set { glyph = value; Size = GetPreferredSize(Size.Empty); Invalidate(); }
    }

    bool ShowsGlyph => !string.IsNullOrEmpty(glyph) && Theme.HasIcons;

    public override Size GetPreferredSize(Size proposed)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        int width = text.Width + Theme.S(look == Look.Ghost ? 12 : 26);
        if (ShowsGlyph) width += TextRenderer.MeasureText(glyph, Theme.Icon, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(7);
        return new Size(width, Math.Max(Theme.S(32), text.Height + Theme.S(14)));
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
        Color back, fore, border;
        switch (look)
        {
            case Look.Primary:
                back = pressed ? Theme.AccentDark : hover ? Color.FromArgb(29, 88, 225) : Theme.Accent;
                fore = Color.White;
                border = back;
                break;
            case Look.Danger:
                back = pressed ? Color.FromArgb(153, 27, 27) : hover ? Theme.RedDark : Theme.Red;
                fore = Color.White;
                border = back;
                break;
            case Look.Ghost:
                back = pressed ? Color.FromArgb(215, 222, 232) : hover ? Color.FromArgb(229, 233, 240) : parent;
                fore = Theme.Accent;
                border = back;
                break;
            default:
                back = pressed ? Color.FromArgb(229, 231, 235) : hover ? Color.FromArgb(243, 244, 246) : Color.White;
                fore = Theme.Text;
                border = Color.FromArgb(203, 208, 216);
                break;
        }
        if (!Enabled)
        {
            back = look == Look.Ghost ? parent : Color.FromArgb(232, 234, 238);
            fore = Theme.Gray;
            border = look == Look.Ghost ? parent : Color.FromArgb(225, 228, 233);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(6)))
        using (var brush = new SolidBrush(back))
        using (var pen = new Pen(border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = SmoothingMode.Default;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine;
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
        int glyphWidth = 0;
        Size glyphSize = Size.Empty;
        if (ShowsGlyph)
        {
            glyphSize = TextRenderer.MeasureText(g, glyph, Theme.Icon, Size.Empty, flags);
            glyphWidth = glyphSize.Width + Theme.S(7);
        }
        int x = Math.Max(Theme.S(4), (Width - glyphWidth - textSize.Width) / 2);
        if (ShowsGlyph)
            TextRenderer.DrawText(g, glyph, Theme.Icon, new Rectangle(x, 1, glyphSize.Width + 2, Height), fore, back, flags);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphWidth, 0, Width - x - glyphWidth, Height), fore, back,
                              flags | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -Theme.S(3), -Theme.S(3)));
    }
}

/// <summary>An entry of the navigation bar on the left.</summary>
sealed class NavButton : Button
{
    readonly string glyph;
    bool hover, selected;

    public NavButton(string text, string glyph)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        this.glyph = glyph;
        Text = text;
        AccessibleName = text;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Ui;
        Cursor = Cursors.Hand;
        Margin = Padding.Empty;
        Height = Theme.S(46);
    }

    public bool Selected
    {
        get => selected;
        set { selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = selected ? Theme.NavSelected : hover ? Theme.NavHover : Theme.Nav;
        g.Clear(back);
        if (selected)
            using (var accent = new SolidBrush(Color.FromArgb(96, 165, 250)))
                g.FillRectangle(accent, 0, Theme.S(8), Theme.S(4), Height - Theme.S(16));
        var fore = selected ? Color.White : Color.FromArgb(203, 213, 225);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine;
        if (Theme.HasIcons)
            TextRenderer.DrawText(g, glyph, Theme.IconBig, new Rectangle(Theme.S(20), 1, Theme.S(28), Height), fore, back, flags);
        TextRenderer.DrawText(g, Text, selected ? Theme.Bold : Theme.Ui,
                              new Rectangle(Theme.S(Theme.HasIcons ? 54 : 20), 0, Width, Height), fore, back, flags);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -Theme.S(2), -Theme.S(2)));
    }
}

/// <summary>A tab of the console bar.</summary>
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
        Margin = new Padding(0, 0, Theme.S(2), 0);
        Size = new Size(TextRenderer.MeasureText(text, Theme.Bold).Width + Theme.S(40), Theme.S(36));
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

    /// <summary>Colour of the state dot (Color.Empty = none).</summary>
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
        var back = selected ? Theme.ConsoleBg : hover ? Color.FromArgb(52, 52, 56) : Theme.ConsoleBar;
        g.Clear(back);
        if (selected)
            using (var accent = new SolidBrush(Color.FromArgb(96, 165, 250)))
                g.FillRectangle(accent, 0, 0, Width, Theme.S(2));
        int x = Theme.S(12);
        if (dot != Color.Empty)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Theme.S(8);
            using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, x, (Height - d) / 2, d, d);
            g.SmoothingMode = SmoothingMode.Default;
        }
        x += Theme.S(14);
        TextRenderer.DrawText(g, Text, selected ? Theme.Bold : Theme.Ui, new Rectangle(x, 0, Width - x, Height),
                              selected ? Color.White : Color.FromArgb(180, 180, 188), back,
                              TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

/// <summary>A white panel with rounded corners and an optional title.</summary>
class Card : Panel
{
    public string Title;

    public Card(string title = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Title = title;
        BackColor = Theme.Card;
        Padding = title == null ? Theme.P(14, 12, 14, 10) : Theme.P(14, 38, 14, 10);
        Margin = Theme.P(0, 0, 10, 10);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(8)))
        using (var brush = new SolidBrush(Theme.Card))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = SmoothingMode.Default;
        if (Title != null)
            TextRenderer.DrawText(g, Title, Theme.Bold, new Point(Theme.S(14), Theme.S(12)), Theme.Text, Theme.Card,
                                  TextFormatFlags.NoPadding);
    }
}

/// <summary>One of the three boxes at the top: a coloured dot, a title, the state and a detail line.</summary>
sealed class StatusCard : Control
{
    readonly string title, glyph;
    string main = "", detail = "";
    Color dot = Theme.Gray;
    bool hover;

    public StatusCard(string title, string glyph)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        this.title = title;
        this.glyph = glyph;
        Cursor = Cursors.Hand;
        Margin = Theme.P(0, 0, 10, 0);
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = title;
    }

    public void Set(Color dotColor, string mainText, string detailText)
    {
        if (dot == dotColor && main == mainText && detail == detailText) return;
        dot = dotColor;
        main = mainText ?? "";
        detail = detailText ?? "";
        AccessibleDescription = main + " - " + detail;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var back = hover ? Color.FromArgb(248, 250, 252) : Color.White;
        using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(8)))
        using (var brush = new SolidBrush(back))
        using (var pen = new Pen(hover ? Color.FromArgb(180, 190, 205) : Theme.Border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        int pad = Theme.S(10), d = Theme.S(11), top = (Height - Theme.S(52)) / 2;
        using (var halo = new SolidBrush(Color.FromArgb(50, dot)))
            g.FillEllipse(halo, pad - Theme.S(3), top + Theme.S(2) - Theme.S(3), d + Theme.S(6), d + Theme.S(6));
        using (var brush = new SolidBrush(dot))
            g.FillEllipse(brush, pad, top + Theme.S(2), d, d);
        g.SmoothingMode = SmoothingMode.Default;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        int x = pad + d + Theme.S(10), width = Width - x - pad - Theme.S(22);
        TextRenderer.DrawText(g, title.ToUpperInvariant(), Theme.SmallBold, new Rectangle(x, top, width, Theme.S(15)),
                              Theme.Muted, back, flags);
        TextRenderer.DrawText(g, main, Theme.Bold, new Rectangle(x, top + Theme.S(15), width + Theme.S(22), Theme.S(19)), Theme.Text, back, flags);
        TextRenderer.DrawText(g, detail, Theme.Small, new Rectangle(x, top + Theme.S(35), width + Theme.S(22), Theme.S(17)), Theme.Muted, back, flags);
        if (Theme.HasIcons)
            TextRenderer.DrawText(g, glyph, Theme.IconBig, new Rectangle(Width - pad - Theme.S(20), top - Theme.S(1), Theme.S(22), Theme.S(22)),
                                  Color.FromArgb(203, 208, 216), back, TextFormatFlags.NoPadding);
    }
}

/// <summary>The app's logo (the same drawing as EdgeControl.ico): a low-poly mesh on a blue-violet tile.</summary>
static class Logo
{
    public static void Draw(Graphics g, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(r, (int)(r.Width * 0.22)))
        using (var brush = new LinearGradientBrush(r, Color.FromArgb(59, 130, 246), Color.FromArgb(124, 58, 237), 45f))
            g.FillPath(brush, path);
        float cx = r.X + r.Width * 0.5f, cy = r.Y + r.Height * 0.52f, radius = r.Width * 0.31f;
        var points = Enumerable.Range(0, 6).Select(i =>
        {
            var a = (-90 + 60 * i) * Math.PI / 180;
            return new PointF(cx + radius * (float)Math.Cos(a), cy + radius * (float)Math.Sin(a));
        }).ToArray();
        float w = Math.Max(1.2f, r.Width * 0.055f);
        using (var pen = new Pen(Color.White, w))
        using (var spoke = new Pen(Color.FromArgb(200, 255, 255, 255), w * 0.75f))
        using (var dot = new SolidBrush(Color.White))
        {
            for (int i = 0; i < 6; i++)
            {
                g.DrawLine(pen, points[i], points[(i + 1) % 6]);
                g.DrawLine(spoke, new PointF(cx, cy), points[i]);
            }
            foreach (var p in points.Concat(new[] { new PointF(cx, cy) }))
                g.FillEllipse(dot, p.X - w * 1.25f, p.Y - w * 1.25f, w * 2.5f, w * 2.5f);
        }
        g.SmoothingMode = SmoothingMode.Default;
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
        Text = text, AutoSize = true, Font = font ?? Theme.Ui, ForeColor = color ?? Theme.Text,
        BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, Theme.S(4)),
        MaximumSize = maxWidth > 0 ? new Size(Theme.S(maxWidth), 0) : Size.Empty, UseMnemonic = false,
    };

    public static RadioButton Radio(string text, bool bold = false) => new RadioButton
    {
        Text = text, AutoSize = true, Font = bold ? Theme.Bold : Theme.Ui, ForeColor = Theme.Text,
        Margin = new Padding(0, Theme.S(2), Theme.S(8), Theme.S(1)), UseMnemonic = false, Cursor = Cursors.Hand,
    };

    public static CheckBox Check(string text, bool value = false) => new CheckBox
    {
        Text = text, AutoSize = true, Checked = value, Font = Theme.Ui, ForeColor = Theme.Text,
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
        var label = Label(text, Theme.Ui, color ?? Theme.Text);
        label.Margin = new Padding(0, Theme.S(5), Theme.S(6), 0);
        return label;
    }

    /// <summary>A card whose height follows its content (a stack inside).</summary>
    public static Card CardOf(string title, params Control[] content)
    {
        var card = new Card(title) { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        var stack = Stack(content);
        stack.Location = new Point(card.Padding.Left, card.Padding.Top);
        card.Controls.Add(stack);
        return card;
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
                rows[i].control.Dock = rows[i].percent > 0 || rows[i].control is TableLayoutPanel || rows[i].control is Card
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

/// <summary>A page of the panel (one entry of the navigation bar).</summary>
abstract class Page : Panel
{
    protected readonly MainForm App;
    public abstract string NavText { get; }
    public abstract string NavGlyph { get; }

    protected Page(MainForm app)
    {
        App = app;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = Theme.P(18, 10, 8, 4);
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

    /// <summary>The page title.</summary>
    protected static Control Header(string title)
    {
        var name = Make.Label(title, Theme.Title);
        name.Margin = new Padding(0, 0, 0, Theme.S(8));
        return name;
    }

    public virtual void Showing() { }

    /// <summary>Server, headset, PC or tasks changed: enable / disable, refresh the texts.</summary>
    public virtual void StatusChanged() { }
}
