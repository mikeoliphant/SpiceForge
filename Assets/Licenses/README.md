# Bundled licenses

License texts for what SpiceForgeServer ships that has no license file in this repository: copied verbatim from
upstream, and checked against it. SpiceForgeServer's build copies these, with SpiceForge's own `LICENSE` and the
submodules' (LiveSPICE, ComputerAlgebra, JackSharpCore), into `licenses/` beside the server, which serves them to the
editor's About dialog. `SpiceForgeServer/About.cs` lists the components and which file is whose.

- `NAudio.txt`: https://github.com/naudio/NAudio/blob/master/license.txt (NAudio.Core and NAudio.Asio).
- `dotnet.txt`: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT, the same text as
  https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt: the .NET runtime and ASP.NET Core, which a
  self-contained package includes.
