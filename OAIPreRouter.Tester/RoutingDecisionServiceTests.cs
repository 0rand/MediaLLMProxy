using Xunit;
using OAIPreRouter.Cli.Services;
using OAIPreRouter.Cli.Models;

namespace OAIPreRouter.Cli.Tests;

public class RoutingDecisionServiceTests
{
    private RoutingOptions CreateOptions(int systemPromptThreshold = 100000, int fastLaneThreshold = 65536)
    {
        return new RoutingOptions
        {
            SystemPromptThresholdBytes = systemPromptThreshold,
            FastLaneThresholdBytes = fastLaneThreshold
        };
    }

    private string CreateMainAgentPayload(int systemPromptSizeBytes)
    {
        var systemContent = new string('x', systemPromptSizeBytes);
        return $"{{\"model\":\"test\",\"messages\":[{{\"role\":\"system\",\"content\":\"{systemContent}\"}},{{\"role\":\"user\",\"content\":\"Hello\"}}]}}";
    }

    private string CreateSubAgentPayload(int bodySizeBytes)
    {
        var smallSystem = "You are a test agent.";
        var filler = new string('y', bodySizeBytes);
        return $"{{\"model\":\"test\",\"messages\":[{{\"role\":\"system\",\"content\":\"{smallSystem}\"}},{{\"role\":\"user\",\"content\":\"{filler}\"}}]}}";
    }

    [Fact]
    public void AnalyzeRequest_LargeSystemPrompt_ReturnsMainAgent()
    {
        // Arrange
        var opts = CreateOptions();
        var payload = CreateMainAgentPayload(150000); // > 100KB threshold

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.True(result.IsMainAgent);
        Assert.Equal("system", result.FirstMessageRole);
        Assert.True(result.FirstMessageBytes > 100000);
    }

    [Fact]
    public void AnalyzeRequest_SmallSystemPrompt_ReturnsSubAgent()
    {
        // Arrange
        var opts = CreateOptions();
        var payload = CreateSubAgentPayload(1000); // small system + small body

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.False(result.IsMainAgent);
        Assert.Equal("system", result.FirstMessageRole);
    }

    [Fact]
    public void AnalyzeRequest_EmptyMessagesArray_ReturnsMainAgent()
    {
        // Arrange
        var opts = CreateOptions();
        var payload = "{\"model\":\"test\",\"messages\":[]}";

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.True(result.IsMainAgent);
        Assert.Equal("empty", result.FirstMessageRole);
    }

    [Fact]
    public void AnalyzeRequest_NoMessagesProperty_ReturnsMainAgent()
    {
        // Arrange
        var opts = CreateOptions();
        var payload = "{\"model\":\"test\"}";

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.True(result.IsMainAgent);
        Assert.Equal("unknown", result.FirstMessageRole);
    }

    [Fact]
    public void AnalyzeRequest_NonSystemFirstMessage_ReturnsSubAgent()
    {
        // Arrange
        var opts = CreateOptions();
        var payload = "{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}";

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.False(result.IsMainAgent);
        Assert.Equal("user", result.FirstMessageRole);
    }

    [Fact]
    public void AnalyzeRequest_MalformedJson_ReturnsMainAgent()
    {
        // Arrange
        var opts = CreateOptions();
        var payload = "not valid json {{{";

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.True(result.IsMainAgent);
        Assert.Equal("parse_error", result.FirstMessageRole);
    }

    [Fact]
    public void AnalyzeRequest_SubAgentBelowThreshold_ClassifiesAsFast()
    {
        // Arrange
        var opts = CreateOptions(fastLaneThreshold: 65536);
        var payload = CreateSubAgentPayload(50000); // < 65536

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.False(result.IsMainAgent);
        Assert.True(result.TotalBytes <= opts.FastLaneThresholdBytes);
    }

    [Fact]
    public void AnalyzeRequest_SubAgentAboveThreshold_ClassifiesAsHeavy()
    {
        // Arrange
        var opts = CreateOptions(fastLaneThreshold: 65536);
        var payload = CreateSubAgentPayload(100000); // > 65536

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.False(result.IsMainAgent);
        Assert.True(result.TotalBytes > opts.FastLaneThresholdBytes);
    }

    [Fact]
    public void DecideRoute_MainAgent_ReturnsPrimary()
    {
        // Arrange
        var opts = CreateOptions();
        var payload = CreateMainAgentPayload(150000);
        var limiter = new ConnectionLimiter();

        // Act
        var decision = RoutingDecisionService.DecideRoute(payload, opts, limiter);

        // Assert
        Assert.True(decision.IsPrimary);
        Assert.Contains("MainAgent", decision.Reason);
    }

    [Fact]
    public void DecideRoute_SubAgent_ReturnsPrimary_Phase1()
    {
        // Arrange
        var opts = CreateOptions();
        var payload = CreateSubAgentPayload(5000);
        var limiter = new ConnectionLimiter();

        // Act
        var decision = RoutingDecisionService.DecideRoute(payload, opts, limiter);

        // Assert
        Assert.True(decision.IsPrimary);
        Assert.Contains("SubAgent", decision.Reason);
    }

    [Fact]
    public void AnalyzeRequest_SystemPromptAtThresholdBoundary_ReturnsMainAgent()
    {
        // Arrange
        var threshold = 100000;
        var opts = CreateOptions(systemPromptThreshold: threshold);
        var systemContent = new string('x', threshold + 1); // Just over threshold
        var payload = $"{{\"model\":\"test\",\"messages\":[{{\"role\":\"system\",\"content\":\"{systemContent}\"}},{{\"role\":\"user\",\"content\":\"Hi\"}}]}}";

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.True(result.IsMainAgent);
    }

    [Fact]
    public void AnalyzeRequest_SystemPromptBelowThreshold_ReturnsSubAgent()
    {
        // Arrange
        var threshold = 100000;
        var opts = CreateOptions(systemPromptThreshold: threshold);
        var systemContent = new string('x', threshold - 1); // Just under threshold
        var payload = $"{{\"model\":\"test\",\"messages\":[{{\"role\":\"system\",\"content\":\"{systemContent}\"}},{{\"role\":\"user\",\"content\":\"Hi\"}}]}}";

        // Act
        var result = RoutingDecisionService.AnalyzeRequest(payload, opts);

        // Assert
        Assert.False(result.IsMainAgent);
    }
}
