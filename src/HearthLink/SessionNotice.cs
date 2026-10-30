using HearthLink.Core;
namespace HearthLink;
/// <summary>
/// Persistent, visible local indicator that does not steal keyboard focus. Shortly after a session starts it collapses to a
/// small tab in the corner ("● name • Control"); clicking the tab brings back End session. It never disappears while the session lasts.
/// </summary>
public sealed class SessionNotice : Form
{
    bool ended;
    readonly Panel full=new() {Dock=DockStyle.Fill};
    readonly Label tab=new() {Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Padding=new(10,0,8,0),Font=new("Segoe UI",9.5f,FontStyle.Bold),ForeColor=Color.White,Cursor=Cursors.Hand,Visible=false,AutoEllipsis=true};
    readonly System.Windows.Forms.Timer collapse=new();
    public Button EndButton { get; }
    public bool Collapsed { get; private set; }
    protected override bool ShowWithoutActivation=>true;
    public SessionNotice(string controller,bool control,Action endSession,TimeSpan? collapseAfter=null,Func<Point>? pointerPosition=null)
    {
        Text="HearthLink • active connection"; FormBorderStyle=FormBorderStyle.FixedToolWindow;
        ControlBox=false; ShowInTaskbar=false; TopMost=true; BackColor=Brand.Navy; ForeColor=Color.White; Font=new("Segoe UI",10); Icon=Brand.AppIcon;
        var label=new Label {Text=SessionNoticeText.Message(controller,control),Left=18,Top=14,Width=350,Height=72,ForeColor=Color.White,Font=new("Segoe UI",11,FontStyle.Bold)};
        EndButton=Brand.Style(new Button {Text="End session",Left=18,Top=104,Width=350,Height=36},true);
        EndButton.Click+=(_,_)=>endSession(); full.Controls.AddRange([label,EndButton]);
        tab.Text="● "+SessionNoticeText.Controller(controller)+(control?" • Control":" • View only")+"  ▴";
        tab.Click+=(_,_)=>Expand();
        Controls.AddRange([full,tab]);
        StartPosition=FormStartPosition.Manual;
        collapse.Interval=(int)(collapseAfter??TimeSpan.FromSeconds(5)).TotalMilliseconds;
        // Stays open while the pointer is over the full panel, then folds back into the tab.
        var pointer=pointerPosition??(()=>Cursor.Position);
        collapse.Tick+=(_,_)=> { if(!Bounds.Contains(pointer())) Collapse(); };
        FormClosing+=(_,e)=> {if(!ended) e.Cancel=true;};
        Disposed+=(_,_)=>collapse.Dispose();
        Expand();
    }
    /// <summary>The full panel with End session, in the bottom-right corner of the primary screen.</summary>
    public void Expand()
    {
        Collapsed=false; tab.Visible=false; full.Visible=true;
        FormBorderStyle=FormBorderStyle.FixedToolWindow; ClientSize=new(390,154); Place();
        collapse.Stop(); collapse.Start();
    }
    /// <summary>A small tab that keeps the session visible without covering the desktop.</summary>
    public void Collapse()
    {
        collapse.Stop(); Collapsed=true; full.Visible=false; tab.Visible=true;
        FormBorderStyle=FormBorderStyle.None; Size=new(260,34); Place();
    }
    void Place() { var area=Screen.PrimaryScreen!.WorkingArea; Location=new(area.Right-Width-18,area.Bottom-Height-18); }
    public void CloseAfterSession() {ended=true; collapse.Stop(); Close(); Dispose();}
}
