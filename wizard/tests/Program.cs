using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrayonSetup;
using System.IO;
using System.IO.Compression;
class FakeHost : ISetupHost
{
 bool running;
 // Rhino's process ends after this many more looks at it (never, when negative).
 public int ExitsAfterChecks = -1;
 public bool RhinoRunning { get { if(ExitsAfterChecks>=0&&ExitsAfterChecks--==0) running=false; return running; } set { running=value; } }
 public bool RhinoClosing { get; set; }
 public bool FailMcp, OpenDuringDownload, FailVerify;
 public readonly List<string> Calls = new List<string>();
 readonly HashSet<string> installed = new HashSet<string>();
 Task Step(string name) { if(installed.Add(name)) Calls.Add(name); return Task.CompletedTask; }
 public Task EnsureWebView(CancellationToken t) => Step("webview");
 public Task EnsureMcp(CancellationToken t) { if(FailMcp) throw new Exception("Network unavailable"); return Step("mcp"); }
 public Task EnsureAgent(string name,CancellationToken t) { if(OpenDuringDownload) RhinoRunning=true; return Step(name); }
 public Task InstallPlugin(CancellationToken t) => Step("plugin");
 public void VerifyPlugin() { if(FailVerify) throw new Exception("Verification failed"); Calls.Add("verified"); }
}
class Program
{
 static void Check(bool pass,string text) { if(!pass) throw new Exception(text); Console.WriteLine("PASS "+text); }
 static async Task<bool> Run(FakeHost host, double waitSeconds=120)
 {
  var installer=new Installer(host){ClosingWait=TimeSpan.FromSeconds(waitSeconds),ClosingPoll=TimeSpan.FromMilliseconds(5)};
  try {await installer.Install(new SetupOptions(),(p,s)=>{},CancellationToken.None);return true;}catch{return false;}
 }
 static int Main(string[] args) {try{Test().GetAwaiter().GetResult();if(args.Contains("--local-check"))LocalCheck().GetAwaiter().GetResult();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static async Task LocalCheck()
 {
  string rhino = SystemHost.FindRhino();
  Check(rhino!=null,"Real Rhino 8 detection");
  Check(SystemHost.HasWebView(),"Real WebView2 detection");
  using(var host=new SystemHost(rhino))
  {
   string packages=await host.Run(new[]{Path.Combine(Path.GetDirectoryName(rhino),"Yak.exe"),"list"},CancellationToken.None);
   Check(SystemHost.HasPackage(packages,"Rhino-MCP-Platform",new Version(0,4,0)),"Real RhinoMCP package recognition");
   foreach(string provider in new[]{"claude","codex"})
   {
    var command=SystemHost.AgentCommand(provider); Check(command!=null,"Existing "+provider+" detected");
    Check((await host.Run(command.Concat(new[]{"--version"}).ToArray(),CancellationToken.None)).Length>0,"Existing "+provider+" executable works");
   }
   using(var payload=SystemHost.Resource("plugin.yak"))
   using(var zip=new ZipArchive(payload,ZipArchiveMode.Read))
   {
    var plugin=zip.GetEntry("RhinoSketchingTool.rhp");Check(plugin!=null,"Single installer embeds plugin package");
    string copy=Path.Combine(host.Work,"check.rhp");using(var output=File.Create(copy))using(var input=plugin.Open())input.CopyTo(output);
    Check(SystemHost.ResourceText("package-info.txt").Contains(SystemHost.Hash(copy)),"Embedded payload hash matches installation verification");
   }
  }
 }
 static async Task Test()
 {
  var h=new FakeHost();Check(await Run(h)&&h.Calls.SequenceEqual(new[]{"webview","mcp","claude","codex","plugin","verified"}),"Fresh install prepares dependencies before committing plugin");
  Check(await Run(h)&&h.Calls.Count(c=>c=="plugin")==1,"Repeated setup reuses installed components");
  h=new FakeHost{RhinoRunning=true};Check(!await Run(h)&&h.Calls.Count==0,"Open Rhino prevents all installation changes");
  h=new FakeHost{FailMcp=true};Check(!await Run(h)&&!h.Calls.Contains("plugin"),"Dependency failure never reports a completed installation");
  h.FailMcp=false;Check(await Run(h)&&h.Calls.Count(c=>c=="webview")==1,"Retry continues after completed components");
  h=new FakeHost{OpenDuringDownload=true};Check(!await Run(h)&&!h.Calls.Contains("plugin"),"Rhino opening during download blocks plugin commit");
  h=new FakeHost{FailVerify=true};Check(!await Run(h),"Verification failure cannot become success");
  h=new FakeHost{RhinoRunning=true,RhinoClosing=true,ExitsAfterChecks=6};Check(await Run(h)&&h.Calls.Contains("plugin"),"Setup waits for a Rhino that is already closing");
  h=new FakeHost{RhinoRunning=true,RhinoClosing=true};Check(!await Run(h,0.05)&&h.Calls.Count==0,"A Rhino that never finishes closing still blocks installation");
  var clock=System.Diagnostics.Stopwatch.StartNew();h=new FakeHost{RhinoRunning=true};Check(!await Run(h)&&clock.Elapsed.TotalSeconds<5,"An open Rhino is reported at once, without waiting");
  PackageTests.Run(Check);
  Check(!SystemHost.HasPackage("Rhino-MCP-Platform (0.3.9)","Rhino-MCP-Platform",new Version(0,4,0)),"Outdated RhinoMCP requires an upgrade");
  Check(!SystemHost.HasPackage("Fake-Rhino-MCP-Platform (0.4.0)","Rhino-MCP-Platform",new Version(0,4,0)),"Package detection requires an exact name");
 }
}
