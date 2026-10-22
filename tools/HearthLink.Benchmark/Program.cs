using HearthLink;
using HearthLink.Core;
using System.Diagnostics;
using System.Text.Json;

string output=args.FirstOrDefault(a=>a.EndsWith(".json",StringComparison.OrdinalIgnoreCase))??"artifacts/performance-baseline.json";
if(args.Contains("--optimized"))
{
    var results=new List<object>();
    for(int profile=0;profile<3;profile++)
    {
        using var capture=new DesktopCapture();
        var captures=new List<double>(); var decodes=new List<double>(); var cycles=new List<double>(); var sizes=new List<int>();
        using var target=new Bitmap(Screen.PrimaryScreen!.Bounds.Width,Screen.PrimaryScreen.Bounds.Height); using var painter=Graphics.FromImage(target);
        int width=0,height=0;
        for(int i=0;i<35;i++)
        {
            var cycle=Stopwatch.StartNew(); var stage=Stopwatch.StartNew(); var frame=capture.Capture(0,i+1,profile); stage.Stop(); width=frame.Width; height=frame.Height;
            var decode=Stopwatch.StartNew(); using var wire=new MemoryStream(); await Wire.Write(wire,new("frame",Frame:frame)); wire.Position=0;
            var received=await Wire.Read(wire); using var image=FrameDecoder.Decode(received.Frame!); painter.DrawImage(image,0,0,target.Width,target.Height); decode.Stop();
            double remaining=1000d/DesktopCapture.Profile(profile).Fps-cycle.Elapsed.TotalMilliseconds; if(remaining>1) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
            cycle.Stop(); if(i>=5) {captures.Add(stage.Elapsed.TotalMilliseconds); decodes.Add(decode.Elapsed.TotalMilliseconds); cycles.Add(cycle.Elapsed.TotalMilliseconds); sizes.Add((int)wire.Length);}
        }
        results.Add(new {profile=new[]{"Responsive","Balanced","Full detail"}[profile],resolution=$"{width}x{height}",samples=cycles.Count,captureEncodeMs=Median(captures),serializeDecodeDrawMs=Median(decodes),frameIntervalMs=Median(cycles),pipelineFps=1000/Median(cycles),averageWireBytes=sizes.Average()});
    }
    var report=new {build="0.2.0 candidate",results,physicalPcTest=false,notes="Local live capture, binary memory transport, decode and CPU draw. No network or display-photon measurement; lower-resolution modes trade detail for responsiveness. No images saved."};
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true})); Console.WriteLine(File.ReadAllText(output)); return;
}
var timings=new List<double>(); var frames=new List<double>(); var bytes=new List<int>(); var wireTimes=new List<double>();
using var render=new Bitmap(Screen.PrimaryScreen!.Bounds.Width,Screen.PrimaryScreen.Bounds.Height);
using var drawing=Graphics.FromImage(render);
for(int i=0;i<35;i++)
{
    var cycle=Stopwatch.StartNew(); var capture=Stopwatch.StartNew();
    byte[] jpeg=Desktop.Capture(0); capture.Stop();
    var serialization=Stopwatch.StartNew();
    using var stream=new MemoryStream();
    await Wire.Write(stream,new("frame",Data:Convert.ToBase64String(jpeg)));
    stream.Position=0; var received=await Wire.Read(stream);
    using var encoded=new MemoryStream(Convert.FromBase64String(received.Data!));
    using var image=Image.FromStream(encoded); using var decoded=new Bitmap(image);
    drawing.DrawImage(decoded,0,0,render.Width,render.Height); serialization.Stop();
    await Task.Delay(100); cycle.Stop();
    if(i>=5) {timings.Add(capture.Elapsed.TotalMilliseconds); frames.Add(cycle.Elapsed.TotalMilliseconds); bytes.Add((int)stream.Length); wireTimes.Add(serialization.Elapsed.TotalMilliseconds);}
}
double Median(IEnumerable<double> source) {var a=source.Order().ToArray();return a[a.Length/2];}
var result=new {build="0.1.1 baseline",samples=frames.Count,screen=$"{render.Width}x{render.Height}",captureEncodeMs=Median(timings),serializeDecodeDrawMs=Median(wireTimes),frameIntervalMs=Median(frames),pipelineFps=1000/Median(frames),averageWireBytes=bytes.Average(),physicalPcTest=false,notes="Local live capture into memory only; no screen files or contents saved. Includes legacy 100ms pacing; not network or display-photon latency."};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine(File.ReadAllText(output));
