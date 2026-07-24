using Sonulab.Core.Transport;
using Xunit;

public class SendBatchDefaultTests
{
    /// <summary>A link that implements ONLY SendAsync — exactly the position TcpSonuLink and the
    /// fakes are in. It must still answer SendBatchAsync correctly, via the default lockstep
    /// implementation on the interface.</summary>
    private sealed class SequentialOnlyLink : ISonuLink
    {
        public readonly List<string> Sent = new();
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult($"resp:{command}\r\n");
        }
    }

    [Fact]
    public async Task Default_SendBatchAsync_is_one_SendAsync_per_command_in_order()
    {
        var impl = new SequentialOnlyLink();
        ISonuLink link = impl;
        var windows = await link.SendBatchAsync(new[] { "a", "b", "c" });
        Assert.Equal(new[] { "a", "b", "c" }, impl.Sent);
        Assert.Equal(new[] { "resp:a\r\n", "resp:b\r\n", "resp:c\r\n" }, windows);
    }

    [Fact]
    public async Task Default_SendBatchAsync_handles_an_empty_command_list()
    {
        ISonuLink link = new SequentialOnlyLink();
        Assert.Empty(await link.SendBatchAsync(Array.Empty<string>()));
    }

    [Fact]
    public void Pipelining_defaults_match_the_probe_proven_values()
    {
        var o = new SerialLinkOptions();
        Assert.True(o.PipelineEnabled);
        Assert.Equal(30, o.PipelineMinPaceMs);   // PROTOCOL.md: 25 ms is the cliff
        Assert.Equal(3, o.PipelinePollMs);
    }
}
