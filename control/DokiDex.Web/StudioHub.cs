using Microsoft.AspNetCore.SignalR;

namespace DokiDex.Web;

// The live channel to the browser. P1 bridges SwarmUI's GenerateText2ImageWS frames (queued / progress +
// base64 preview / result) onto this hub so generation cards fill in real time. Empty for the P0 shell.
public sealed class StudioHub : Hub { }
