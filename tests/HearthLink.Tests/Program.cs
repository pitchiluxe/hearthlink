using HearthLink.Core;
using System.Buffers.Binary;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
var gate = new PairingGate();
Check(!gate.Consume("00000000", DateTimeOffset.UtcNow), "pairing closed by default");
var now = DateTimeOffset.UtcNow;
string code = gate.Open(now);
Check(code.Length == 8 && code.All(char.IsAsciiDigit), "eight random decimal digits");
Check(!gate.Consume("wrong", now), "wrong code rejected");
Check(gate.Consume(code, now), "valid code accepted");
Check(!gate.Consume(code, now), "code cannot be replayed");
code = gate.Open(now);
Check(!gate.Consume(code, now.AddMinutes(3)), "expired code rejected");
code = gate.Open(now);
for (int i=0;i<5;i++) gate.Consume("wrong", now, "10.0.0.9");
Check(!gate.Consume(code, now, "10.0.0.9"), "five failures lock out the guessing peer");
Check(gate.Consume(code, now, "10.0.0.2"), "another peer's failures do not close the pairing window");
code = gate.Open(now);
for (int i=0;i<PairingGate.TotalAttempts;i++) gate.Consume("wrong", now, "10.0.1." + (i/PairingGate.PeerAttempts));
Check(!gate.Consume(code, now, "10.0.0.2"), "total failures across peers close pairing window");
Check(PeerName.Clean("Desk\u202Etxt.exe\u200B") == "Desktxt.exe", "peer name strips bidi override and zero-width characters");
Check(PeerName.Clean("A\u2028B\u2029C\r\n") == "ABC", "peer name strips line and paragraph separators");
Check(PeerName.Clean(new string('x', 100)).Length == PeerName.MaxLength, "peer name is length-limited");
Check(!PeerName.Valid("Desk\u202E") && !PeerName.Valid("") && !PeerName.Valid(" Desk") && PeerName.Valid("Office PC"), "pairing accepts only names that are already clean");
var trust = new TrustBook();
Check(!trust.Contains("A"), "unknown controller rejected");
trust.Add("A", "Desk"); Check(trust.Contains("A"), "authorized controller accepted");
trust.Remove("A"); Check(!trust.Contains("A"), "revoked controller rejected");
using var stream = new MemoryStream();
await Wire.Write(stream, new Packet("hello", Text:"actual message"));
stream.Position=0;
Check((await Wire.Read(stream)).Text == "actual message", "framing round trip");
using var malformed = new MemoryStream(new byte[4]);
bool rejected=false;
try { await Wire.Read(malformed); } catch(InvalidDataException) { rejected=true; }
Check(rejected,"empty frame rejected");
byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, Wire.MaxLength+1);
rejected=false;
try { await Wire.Read(new MemoryStream(header)); } catch(InvalidDataException) { rejected=true; }
Check(rejected,"oversized frame rejected before allocation");
byte[] jpegFixture=Enumerable.Range(0,4096).Select(i=>(byte)(i%256)).ToArray();
using var videoStream=new MemoryStream();
await Wire.Write(videoStream,new("frame",Frame:new(jpegFixture,1920,1080,0,17,1200,2300,8)));
videoStream.Position=0;
var video=await Wire.Read(videoStream);
Check(video.Frame!=null && video.Frame.Sequence==17 && video.Frame.Jpeg.SequenceEqual(jpegFixture),"video frame round trips without Base64");
Check(videoStream.Length==jpegFixture.Length+40,"video frame has only 40 bytes of binary framing overhead");
Check(video.Frame!.Width==1920 && video.Frame.EncodeMicroseconds==2300,"binary frame dimensions and timings preserved");
byte[] damaged=videoStream.ToArray(); BinaryPrimitives.WriteInt32BigEndian(damaged.AsSpan(16),100000);
rejected=false; try {await Wire.Read(new MemoryStream(damaged));} catch(InvalidDataException) {rejected=true;}
Check(rejected,"oversized video dimensions rejected before JPEG allocation");
using(var window=new FrameWindow())
{
    long first=await window.AcquireAsync(); window.MarkSent(first);
    var second=window.AcquireAsync();
    Check(!second.IsCompleted,"slow viewer cannot queue another frame");
    Check(!window.Acknowledge(first+1),"wrong presentation acknowledgement ignored");
    Check(window.Acknowledge(first),"correct presentation acknowledgement releases next frame");
    long next=await second;
    Check(next==first+1 && !window.Acknowledge(first),"stale acknowledgement cannot release a later frame");
    using var cancel=new CancellationTokenSource(); var waiting=window.AcquireAsync(cancel.Token); cancel.Cancel();
    bool cancelled=false; try {await waiting;} catch(OperationCanceledException) {cancelled=true;}
    Check(cancelled,"waiting video producer cancels cleanly");
}
var input=new InputMailbox(4);
for(int i=0;i<10000;i++) CheckQuiet(input.Post(new("move",X:i)));
Check((await input.ReadAsync()).X==9999,"mouse motion coalesces to latest position");
input.Post(new("move",X:7)); input.Post(new("button",Value:1,Down:true)); input.Post(new("button",Value:1));
Check((await input.ReadAsync()).X==7 && (await input.ReadAsync()).Down && !(await input.ReadAsync()).Down,"click retains its preceding pointer position and release ordering");
for(int i=0;i<4;i++) CheckQuiet(input.Post(new("key",Value:65,Down:true)));
Check(!input.Post(new("key",Value:66)),"input queue is bounded during congestion");
Check(SessionNoticeText.Message("Controller\r\nInjected",false)=="ControllerInjected is connected\nView only","connection notification sanitizes controller label and identifies view-only mode");
Check(SessionNoticeText.Message("Desk",true).Contains("Keyboard and mouse control"),"connection notification distinguishes control access");
Check(CaptureSizing.Fit(3840,2160,1280,720)==(1280,720),"responsive mode caps encoded pixels while preserving aspect ratio");
Check(CaptureSizing.Fit(1024,768,1600,900)==(1024,768),"capture never upscales a smaller monitor");
Check(CaptureSizing.Fit(1080,1920,1280,720)==(405,720),"portrait monitor sizing preserves aspect ratio");
Check(!FilePolicy.ValidName("..\\secret.txt") && !FilePolicy.ValidName("../secret.txt"),"remote file names cannot traverse directories");
Check(!FilePolicy.ValidName("C:\\file.txt") && !FilePolicy.ValidName("CON.txt") && !FilePolicy.ValidName("name. "),"remote file names reject absolute paths and Windows reserved names");
Check(FilePolicy.ValidName("Project report.pdf"),"ordinary file name accepted");
await UpdateChecks.Run(Check);
Console.WriteLine($"{passed} checks passed.");
void CheckQuiet(bool value) {if(!value) throw new Exception("Fixture enqueue failed");}
