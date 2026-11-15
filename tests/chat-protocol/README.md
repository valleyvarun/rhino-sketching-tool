# Chat transport checks

These tests run both provider adapters against a deterministic local fixture. They do not sign in, access an account, or call a model. They cover discovery, image and file attachment payloads, streaming, approvals, interruption, resumed conversations, rejected files, transcript isolation, and termination of wrapper child processes.

With the .NET Framework 4.8 developer pack, a .NET SDK, and Node.js installed:

```powershell
dotnet build tests/chat-protocol/ChatProtocol.csproj -c Release
& ./tests/chat-protocol/bin/Release/net48/ChatProtocol.exe (Get-Command node).Source tests/chat-protocol/mock-cli.js
```

On machines where SDK discovery is restricted, pass `-p:TargetPlatformSdkPath='C:/Program Files (x86)/Windows Kits/10/' -p:TargetPlatformDisplayName=Windows` to the build command. All fixture outputs stay under this test's ignored `bin` directory.

Run the process-tree checks in a Windows session that permits `taskkill` to terminate the test's own child processes. A sandbox that prohibits process-tree traversal will intentionally fail these checks.
