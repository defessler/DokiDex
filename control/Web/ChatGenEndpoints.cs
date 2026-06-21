using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DokiDex.Web;

// Chat-thread surfacing of the render round-trip: the SPA chat view polls this to show each chat-queued image gen's
// lifecycle (queued -> rendering -> done/failed) INLINE in the thread that requested it. The generate_image tool
// (ChatTools) enqueues a PendingGen carrying the originating Conversation id; the ChatGenCoordinator drives its
// Status/Preview/ResultRel as the deferred render runs in Media mode. This endpoint is the read side: it returns
// only the pending gens belonging to ONE conversation, newest-first (PendingGenStore.List ordering), as JSON
// (camelCase via Results.Json), so the chat SPA never sees another thread's queued work. Mapped by StudioHost
// next to the other /api groups (ChatGenEndpoints.Map(api)).
public static class ChatGenEndpoints
{
    // GET /api/chat/pending-gens?conversation=<id> — the pending gens for ONE chat thread, newest-first. A
    // missing/blank conversation yields an empty list (a fresh thread has no id yet, so there is nothing to show)
    // rather than leaking every thread's queue. PendingGenStore.List() is the graceful, newest-first source; the
    // pure FilterByConversation does the (case-sensitive, id-exact) narrowing so it can be unit-tested GPU/disk-free.
    public static void Map(this IEndpointRouteBuilder api)
    {
        api.MapGet("/chat/pending-gens", (string? conversation) =>
            Results.Json(FilterByConversation(PendingGenStore.List(), conversation)));
    }

    // PURE: keep only the pending gens whose Conversation EXACTLY matches the requested id (ordinal), preserving the
    // source order (PendingGenStore.List is already newest-first). A null/blank conversation matches nothing — a
    // thread with no server id yet has no queued gens to surface, and we never fall back to "all" (that would leak
    // other conversations' work into this thread). Side-effect-free + total => the unit-test seam.
    public static IReadOnlyList<PendingGen> FilterByConversation(IEnumerable<PendingGen> all, string? conversation)
    {
        if (string.IsNullOrWhiteSpace(conversation)) return System.Array.Empty<PendingGen>();
        return all.Where(p => string.Equals(p.Conversation, conversation, System.StringComparison.Ordinal)).ToList();
    }
}
