using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// Conversation store: on-disk multi-turn persistence (chats/<id>.json), the thing the one-shot LocalLlm
// structurally lacks. Ids are SERVER-generated (no client path => no traversal). Round-trips over the real
// disk store under RepoPaths.Root, creating + cleaning its own chats/ entry.
public class ChatStoreTests
{
    [Fact]
    public void Create_generates_a_server_id_and_save_load_round_trips_messages()
    {
        string? id = null;
        try
        {
            var conv = ChatStore.NewConversation("doki", lorebook: null);
            id = conv.Id;
            Assert.False(string.IsNullOrWhiteSpace(id));   // server-generated, never client-supplied

            conv = conv with
            {
                Messages = new List<ChatTurn>
                {
                    new("user", "hello", "2026-06-18T00:00:00Z"),
                    new("assistant", "hi there", "2026-06-18T00:00:01Z"),
                }
            };
            Assert.True(ChatStore.Save(conv));

            var loaded = ChatStore.Load(id);
            Assert.NotNull(loaded);
            Assert.Equal(id, loaded!.Id);
            Assert.Equal("doki", loaded.Persona);
            Assert.Equal(2, loaded.Messages.Count);
            Assert.Equal("hello", loaded.Messages[0].Content);
            Assert.Equal("assistant", loaded.Messages[1].Role);

            Assert.Contains(ChatStore.List(), c => c.Id == id);
        }
        finally { if (id is not null) ChatStore.Delete(id); }
    }

    [Fact]
    public void KbId_is_null_by_default_and_round_trips_when_a_doc_is_attached()
    {
        string? id = null;
        try
        {
            var conv = ChatStore.NewConversation("doki", lorebook: null);
            id = conv.Id;
            Assert.Null(conv.KbId);   // a fresh thread has no attached KB (the no-KB chat path stays byte-for-byte)

            // attaching a doc marks the thread (first slice: KbId == the conversation id).
            Assert.True(ChatStore.Save(conv with { KbId = id }));
            var loaded = ChatStore.Load(id);
            Assert.NotNull(loaded);
            Assert.Equal(id, loaded!.KbId);
        }
        finally { if (id is not null) ChatStore.Delete(id); }
    }

    [Fact]
    public void Generated_ids_are_unique()
    {
        var a = ChatStore.NewConversation("p", null);
        var b = ChatStore.NewConversation("p", null);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Load_with_an_unsafe_id_returns_null_without_traversal()
    {
        Assert.Null(ChatStore.Load("../../secret"));
        Assert.Null(ChatStore.Load("a/b"));
    }

    [Fact]
    public void Delete_removes_the_conversation()
    {
        var conv = ChatStore.NewConversation("p", null);
        Assert.True(ChatStore.Save(conv));
        Assert.True(ChatStore.Delete(conv.Id));
        Assert.Null(ChatStore.Load(conv.Id));
    }

    // ---- BRANCH: a non-destructive fork — a NEW conversation carrying the source's Persona/Lorebook/KbId and the
    // kept PREFIX (turns up to + including the chosen index); the ORIGINAL is never re-saved (unchanged on disk).
    [Fact]
    public void Branch_forks_a_new_conversation_with_copied_persona_lorebook_kbid_and_the_prefix()
    {
        string? srcId = null, forkId = null;
        try
        {
            var src = ChatStore.NewConversation("doki", lorebook: "world") with
            {
                KbId = "kb-scope",
                Messages = new List<ChatTurn>
                {
                    new("user", "q1", null), new("assistant", "a1", null),
                    new("user", "q2", null), new("assistant", "a2", null),
                }
            };
            srcId = src.Id;
            Assert.True(ChatStore.Save(src));

            // The endpoint's pure body: keep through index 1 (q1 + a1) into a fresh thread.
            var prefix = ChatEdit.BranchAtTurn(src.Messages, 1);
            var fork = ChatStore.NewConversation(src.Persona, src.Lorebook) with { Messages = prefix, KbId = src.KbId };
            forkId = fork.Id;
            Assert.True(ChatStore.Save(fork));

            Assert.NotEqual(srcId, forkId);   // a fresh, server-minted id — no collision

            var loaded = ChatStore.Load(forkId);
            Assert.NotNull(loaded);
            Assert.Equal("doki", loaded!.Persona);
            Assert.Equal("world", loaded.Lorebook);
            Assert.Equal("kb-scope", loaded.KbId);
            Assert.Equal(2, loaded.Messages.Count);
            Assert.Equal("q1", loaded.Messages[0].Content);
            Assert.Equal("a1", loaded.Messages[1].Content);

            // The ORIGINAL is untouched on disk (non-destructive fork).
            var srcReloaded = ChatStore.Load(srcId);
            Assert.NotNull(srcReloaded);
            Assert.Equal(4, srcReloaded!.Messages.Count);
            Assert.Equal("a2", srcReloaded.Messages[3].Content);
        }
        finally
        {
            if (srcId is not null) ChatStore.Delete(srcId);
            if (forkId is not null) ChatStore.Delete(forkId);
        }
    }
}
