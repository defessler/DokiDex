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
}
