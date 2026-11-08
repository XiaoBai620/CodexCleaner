using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class ConversationTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(20_001)]
    [InlineData(200_000)]
    public void ToolArgumentsAndResultsRemainCompleteThroughTheLimit(int length)
    {
        using var fixture = new Fixture();
        var arguments = new string('a', length - 1) + "A";
        var result = new string('r', length - 1) + "R";
        InsertTool(fixture, "commandExecution", new { id = "command", command = arguments, aggregatedOutput = result });

        var call = Assert.Single(Assert.Single(fixture.Cleaner.Conversation(Fixture.Ids[0])).ToolCalls);
        Assert.Equal(arguments, call.Arguments);
        Assert.Equal(result, call.Result);
    }

    [Fact]
    public void ToolArgumentsAndResultsClipOnlyAfterTwoHundredThousandCharacters()
    {
        using var fixture = new Fixture();
        var arguments = new string('a', 200_000);
        var result = new string('r', 200_000);
        InsertTool(fixture, "commandExecution", new { id = "command", command = arguments + "ARGUMENT-END", aggregatedOutput = result + "RESULT-END" });

        var call = Assert.Single(Assert.Single(fixture.Cleaner.Conversation(Fixture.Ids[0])).ToolCalls);
        Assert.StartsWith(arguments, call.Arguments);
        Assert.StartsWith(result, call.Result);
        Assert.DoesNotContain("ARGUMENT-END", call.Arguments);
        Assert.DoesNotContain("RESULT-END", call.Result);
        Assert.Equal($"\n…（超过 {200_000:N0} 字符的部分未显示）", call.Arguments[200_000..]);
        Assert.Equal($"\n…（超过 {200_000:N0} 字符的部分未显示）", call.Result![200_000..]);
    }

    [Fact]
    public void FileDiffKeepsItsTailBeyondTheOldResultLimit()
    {
        using var fixture = new Fixture();
        var diff = new string('+', 50_000) + "DIFF-END";
        InsertTool(fixture, "fileChange", new { id = "change", changes = new[] { new { path = "synthetic.txt", diff } } });

        var call = Assert.Single(Assert.Single(fixture.Cleaner.Conversation(Fixture.Ids[0])).ToolCalls);
        Assert.Equal("synthetic.txt", call.Arguments);
        Assert.Equal("■ synthetic.txt\n" + diff, call.Result);
    }

    [Fact]
    public void ExpandingAToolDisplaysAllRetainedArgumentsAndResults() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var arguments = new string('a', 199_988) + "ARGUMENT-END";
        var result = new string('r', 199_990) + "RESULT-END";
        InsertTool(fixture, "commandExecution", new { id = "command", command = arguments, aggregatedOutput = result });
        using var view = new ChatView { Size = new Size(900, 600) };
        _ = view.Handle;
        view.SetMessages(fixture.Cleaner.Conversation(Fixture.Ids[0]));
        var blocks = (IList)typeof(ChatView).GetField("blocks", PrivateInstance)!.GetValue(view)!;
        Assert.Single(blocks);
        var block = blocks[0]!;
        var layout = block.GetType().GetField("Layout")!;
        Assert.DoesNotContain("RESULT-END", ((TextLayout)layout.GetValue(block)!).Plain);

        typeof(ChatView).GetMethod("Toggle", PrivateInstance)!.Invoke(view, [block]);

        var expanded = ((TextLayout)layout.GetValue(block)!).Plain;
        Assert.Contains(arguments, expanded);
        Assert.Contains(result, expanded);
        Assert.Contains("ARGUMENT-END", expanded);
        Assert.Contains("RESULT-END", expanded);
        Assert.Contains(arguments, view.Transcript());
        Assert.Contains(result, view.Transcript());
    });

    private static void InsertTool(Fixture fixture, string type, object item)
    {
        using var db = fixture.Open("thread_history_1.sqlite");
        Fixture.Exec(db, "DELETE FROM thread_items WHERE thread_id=$p0", Fixture.Ids[0]);
        Fixture.Insert(db, "thread_items", new()
        {
            ["thread_id"] = Fixture.Ids[0], ["turn_id"] = "turn", ["item_id"] = "tool", ["rollout_ordinal"] = 1L,
            ["created_at_ms"] = 1L, ["item_type"] = type, ["item_json"] = JsonSerializer.Serialize(item),
        });
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "The isolated conversation test did not finish within two minutes.");
        failure?.Throw();
    }
}
