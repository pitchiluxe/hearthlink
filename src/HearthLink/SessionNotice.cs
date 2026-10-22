using HearthLink.Core;
namespace HearthLink;
/// <summary>Persistent, visible local indicator that does not steal keyboard focus.</summary>
public sealed class SessionNotice : Form
{
    bool ended;
    protected override bool ShowWithoutActivation=>true;
    public SessionNotice(string controller,bool control,Action endSession)
    {
        Text="HearthLink • active connection"; ClientSize=new(390,154); FormBorderStyle=FormBorderStyle.FixedToolWindow;
        ControlBox=false; ShowInTaskbar=false; TopMost=true; BackColor=Brand.Navy; ForeColor=Color.White; Font=new("Segoe UI",10); Icon=Brand.AppIcon;
        var label=new Label {Text=SessionNoticeText.Message(controller,control),Left=18,Top=14,Width=350,Height=72,ForeColor=Color.White,Font=new("Segoe UI",11,FontStyle.Bold)};
        var end=Brand.Style(new Button {Text="End session",Left=18,Top=104,Width=350,Height=36},true);
        end.Click+=(_,_)=>endSession(); Controls.AddRange([label,end]);
        StartPosition=FormStartPosition.Manual; var area=Screen.PrimaryScreen!.WorkingArea; Location=new(area.Right-Width-18,area.Bottom-Height-18);
        FormClosing+=(_,e)=> {if(!ended) e.Cancel=true;};
    }
    public void CloseAfterSession() {ended=true; Close(); Dispose();}
}
