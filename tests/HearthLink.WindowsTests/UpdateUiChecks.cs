using HearthLink;
using HearthLink.Core;
using System.Net;
using System.Reflection;

static class UpdateUiChecks
{
    sealed class InvalidReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v9.0.0\",\"assets\":[]}")});
    }
    public static async Task Run(MainWindow owner,IdentityStore identity,Action<bool,string> check)
    {
        using(var center=new UpdateCenter(owner,identity,new HttpClient(new InvalidReleaseHandler())))
        {
            await center.Check(true);
            check(true,"invalid release metadata is handled by update coordinator without escaping to UI events");
        }
        bool reset=await owner.OnUi(()=>
        {
            var type=typeof(MainWindow).Assembly.GetType("HearthLink.UpdateWindow")!;
            using var http=new HttpClient();
            using var window=(Form)Activator.CreateInstance(type,owner,identity,new AppUpdates(http),CancellationToken.None,(Func<Task>)(()=>Task.CompletedTask))!;
            var set=type.GetMethod("SetRelease")!;
            var installer=type.GetField("installer",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var button=(Button)type.GetField("download",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            AppRelease Release(int patch)=>new(new(9,0,patch),new($"https://github.com/pitchiluxe/hearthlink/releases/download/v9.0.{patch}/HearthLink-9.0.{patch}-win-x64-setup.exe"),new string('A',64),100);
            set.Invoke(window,[Release(1),"Version 9.0.1",false]);
            installer.SetValue(window,"fixture-only-not-executed.exe"); button.Text="Install update";
            set.Invoke(window,[Release(2),"Version 9.0.2",false]);
            return installer.GetValue(window)==null && button.Text=="Download update";
        });
        check(reset,"new release clears stale installer binding and restores download action");
    }
}
