using System.Drawing.Drawing2D;

namespace HearthLink;
public static class Brand
{
    public static readonly Color Navy=Color.FromArgb(20,37,65);
    public static readonly Color Blue=Color.FromArgb(37,99,235);
    public static readonly Color Ink=Color.FromArgb(31,47,70);
    public static readonly Color Muted=Color.FromArgb(101,116,139);
    public static readonly Color Line=Color.FromArgb(225,232,241);
    public static readonly Color Canvas=Color.FromArgb(242,246,251);
    public static GraphicsPath Round(RectangleF rect,float radius)
    {
        var p=new GraphicsPath(); float d=radius*2;
        p.AddArc(rect.X,rect.Y,d,d,180,90); p.AddArc(rect.Right-d,rect.Y,d,d,270,90);
        p.AddArc(rect.Right-d,rect.Bottom-d,d,d,0,90); p.AddArc(rect.X,rect.Bottom-d,d,d,90,90); p.CloseFigure(); return p;
    }
    public static void DrawMark(Graphics g,Rectangle rect)
    {
        var state=g.Save(); g.SmoothingMode=SmoothingMode.AntiAlias;
        g.TranslateTransform(rect.X,rect.Y); g.ScaleTransform(rect.Width/512f,rect.Height/512f);
        using var tile=Round(new(12,12,488,488),108);
        using var blue=new LinearGradientBrush(new Point(0,0),new Point(512,512),Color.FromArgb(50,117,249),Color.FromArgb(25,72,184));
        g.FillPath(blue,tile);
        using var white=new Pen(Color.White,27) {LineJoin=LineJoin.Round};
        using var cyan=new Pen(Color.FromArgb(117,236,232),27) {LineJoin=LineJoin.Round};
        using var first=Round(new(88,104,234,168),22); g.DrawPath(white,first);
        using var second=Round(new(190,230,234,168),22); g.DrawPath(cyan,second);
        g.DrawLine(white,162,312,205,312); g.DrawLine(white,184,278,184,310);
        g.DrawLine(cyan,307,400,307,428); g.DrawLine(cyan,275,430,340,430);
        g.Restore(state);
    }
    public static Button Style(Button button,bool primary=false,bool danger=false)
    {
        button.FlatStyle=FlatStyle.Flat; button.FlatAppearance.BorderSize=primary?0:1;
        button.FlatAppearance.BorderColor=Line; button.BackColor=primary?Blue:Color.White;
        button.ForeColor=primary?Color.White:danger?Color.FromArgb(183,56,68):Ink;
        button.Font=new Font("Segoe UI",9.5f,FontStyle.Bold);
        button.Padding=new Padding(12,4,12,4); button.MinimumSize=new Size(0,36); button.Margin=new Padding(0,0,8,0);
        button.Cursor=Cursors.Hand; button.UseVisualStyleBackColor=false;
        button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(29,78,216):Color.FromArgb(235,241,250);
        return button;
    }
    public static void StyleInput(TextBox box)
    { box.Font=new("Segoe UI",10); box.BorderStyle=BorderStyle.FixedSingle; box.BackColor=Color.White; box.ForeColor=Ink; box.MinimumSize=new(0,28); }
    public static Icon AppIcon=>new(typeof(Brand).Assembly.GetManifestResourceStream("HearthLink.AppIcon.ico")!);
}
public sealed class BrandMark : Control
{
    public BrandMark() { SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); Brand.DrawMark(e.Graphics,new(0,0,Width,Height)); }
}
public sealed class Surface : Panel
{
    public Surface() { BackColor=Color.White; Padding=new(20); SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen=new Pen(Brand.Line); e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1); }
}
public sealed class EmptyDevices : Control
{
    public EmptyDevices() { BackColor=Color.White; ForeColor=Brand.Muted; SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
        int center=Width/2,top=Math.Max(15,Height/2-78);
        using var wash=new SolidBrush(Color.FromArgb(237,244,255)); g.FillEllipse(wash,center-39,top,78,78);
        using var pen=new Pen(Brand.Blue,2.5f); using var display=Brand.Round(new(center-23,top+21,46,30),5); g.DrawPath(pen,display);
        g.DrawLine(pen,center,top+52,center,top+60); g.DrawLine(pen,center-12,top+61,center+12,top+61);
        using var title=new Font("Segoe UI",13,FontStyle.Bold); using var body=new Font("Segoe UI",10);
        TextRenderer.DrawText(g,"Your next connection starts here",title,new Rectangle(10,top+95,Width-20,30),Brand.Ink,TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g,"Pair a computer below. Only approved PCs appear here.",body,new Rectangle(10,top+130,Width-20,45),Brand.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.WordBreak);
    }
}
