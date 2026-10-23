using LocalDm.Infrastructure.Probe;
using Xunit;

namespace LocalDm.Tests;

public sealed class PlaytestProbeTests
{
    [Fact]
    public void ParseMove_StandardJson_ReturnsCorrectFields()
    {
        var raw = "{\"to\":\"dm\",\"text\":\"你好\",\"ready\":true,\"vote\":\"r1\",\"confused\":false}";
        var move = PlaytestProbe.ParseMove(raw);

        Assert.Equal("dm", move.To);
        Assert.Equal("你好", move.Text);
        Assert.True(move.Ready);
        Assert.Equal("r1", move.Vote);
        Assert.False(move.Confused);
    }

    [Fact]
    public void ParseMove_JsonInCodeBlock_CanParse()
    {
        var raw = "```json\n{\"to\":\"table\",\"text\":\"大家商量一下\",\"ready\":false,\"vote\":null,\"confused\":true}\n```";
        var move = PlaytestProbe.ParseMove(raw);

        Assert.Equal("table", move.To);
        Assert.Equal("大家商量一下", move.Text);
        Assert.False(move.Ready);
        Assert.Null(move.Vote);
        Assert.True(move.Confused);
    }

    [Fact]
    public void ParseMove_PlainText_ReturnsTableWithOriginalText()
    {
        var raw = "我觉得是他";
        var move = PlaytestProbe.ParseMove(raw);

        Assert.Equal("table", move.To);
        Assert.Equal("我觉得是他", move.Text);
        Assert.False(move.Ready);
        Assert.Null(move.Vote);
        Assert.False(move.Confused);
    }

    [Fact]
    public void ParseMove_UnknownTo_TreatedAsTable()
    {
        var raw = "{\"to\":\"shout\",\"text\":\"救命\",\"ready\":false,\"vote\":null,\"confused\":false}";
        var move = PlaytestProbe.ParseMove(raw);

        Assert.Equal("table", move.To);
        Assert.Equal("救命", move.Text);
    }

    [Fact]
    public void ParseMove_NonStringFields_DoesNotThrowAndFallsBack()
    {
        var raw = "{\"to\":7,\"text\":[]}";
        var move = PlaytestProbe.ParseMove(raw);

        Assert.Equal("table", move.To);
        Assert.Equal(raw, move.Text);
        Assert.False(move.Ready);
        Assert.Null(move.Vote);
        Assert.False(move.Confused);
    }

    [Fact]
    public void ParseMove_EmptyString_ReturnsPass()
    {
        var move = PlaytestProbe.ParseMove("   ");

        Assert.Equal("pass", move.To);
        Assert.Equal(string.Empty, move.Text);
        Assert.False(move.Ready);
        Assert.Null(move.Vote);
        Assert.False(move.Confused);
    }

    [Fact]
    public void TallyVotes_WithVotesAndRoles_ReturnsOrderedCounts()
    {
        var votes = new string?[] { "r1", "r2", "r1", null, "x" };
        var roles = new[] { "r1", "r2", "r3" };

        var tally = PlaytestProbe.TallyVotes(votes, roles);

        Assert.Equal("r1=2, r2=1", tally);
    }

    [Fact]
    public void TallyVotes_AllNull_ReturnsFirstRoleWithOneVote()
    {
        var votes = new string?[] { null, null, null };
        var roles = new[] { "r1", "r2", "r3" };

        var tally = PlaytestProbe.TallyVotes(votes, roles);

        Assert.Equal("r1=1", tally);
    }
}
