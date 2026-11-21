using System.Text.Json;
using Anthropic.Models.Messages;
using RemotePC.Core.AI;
using Xunit;

namespace RemotePC.Core.Tests;

public sealed class AnthropicMappingTests
{
    [Theory]
    [InlineData("refusal")]
    [InlineData("tool_use")]
    [InlineData("end_turn")]
    public void Stop_Reason_Round_Trips_To_Wire_Text(string wire)
    {
        var ev = JsonSerializer.Deserialize<RawMessageStreamEvent>(
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + wire + "\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":7}}")!;
        Assert.True(ev.TryPickDelta(out var d));
        Assert.Equal(wire, AnthropicProvider.StopReasonText(d!.Delta.StopReason));
        Assert.Equal(7, d.Usage.OutputTokens);
    }
}
