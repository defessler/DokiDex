using DokiDex.Web;

// Thin standalone entry point for the studio web host. The server itself is defined ONCE in
// StudioHost (in DokiDex.Control), which the WPF panel also hosts in-process — so a release ships a single
// self-contained exe and this project exists only for dev iteration on the SPA without launching WPF:
//   dotnet run --project control/DokiDex.Web -- --port=5111
var port = StudioHost.ResolvePort(args);
StudioHost.Build(port, args).Run();
