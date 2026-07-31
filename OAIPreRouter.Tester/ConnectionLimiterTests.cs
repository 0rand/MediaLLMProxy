using Xunit;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class ConnectionLimiterTests
{
    [Fact]
    public void TryAcquire_BelowLimit_ReturnsTrue()
    {
        // Arrange
        var limiter = new ConnectionLimiter();
        const int maxConcurrent = 3;

        // Act
        var result1 = limiter.TryAcquire("test-backend", maxConcurrent);
        var result2 = limiter.TryAcquire("test-backend", maxConcurrent);
        var result3 = limiter.TryAcquire("test-backend", maxConcurrent);

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.Equal(3, limiter.GetCount("test-backend"));
    }

    [Fact]
    public void TryAcquire_AtLimit_ReturnsFalse()
    {
        // Arrange
        var limiter = new ConnectionLimiter();
        const int maxConcurrent = 2;

        // Act
        var result1 = limiter.TryAcquire("test-backend", maxConcurrent);
        var result2 = limiter.TryAcquire("test-backend", maxConcurrent);
        var result3 = limiter.TryAcquire("test-backend", maxConcurrent); // Should fail

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        Assert.False(result3);
        Assert.Equal(2, limiter.GetCount("test-backend"));
    }

    [Fact]
    public void Release_FreesSlot_AllowsNextAcquire()
    {
        // Arrange
        var limiter = new ConnectionLimiter();
        const int maxConcurrent = 1;

        // Act
        var acquire1 = limiter.TryAcquire("test-backend", maxConcurrent);
        limiter.Release("test-backend");
        var acquire2 = limiter.TryAcquire("test-backend", maxConcurrent);

        // Assert
        Assert.True(acquire1);
        Assert.True(acquire2);
        Assert.Equal(1, limiter.GetCount("test-backend"));
    }

    [Fact]
    public void Release_DoesNotGoBelowZero()
    {
        // Arrange
        var limiter = new ConnectionLimiter();
        const int maxConcurrent = 2;

        // Act
        limiter.Release("test-backend");
        limiter.Release("test-backend");

        // Assert
        Assert.Equal(0, limiter.GetCount("test-backend"));
    }

    [Fact]
    public void TryAcquire_DifferentBackends_Independent()
    {
        // Arrange
        var limiter = new ConnectionLimiter();
        const int maxConcurrent = 1;

        // Act
        var result1 = limiter.TryAcquire("backend-a", maxConcurrent);
        var result2 = limiter.TryAcquire("backend-b", maxConcurrent);
        var result3 = limiter.TryAcquire("backend-a", maxConcurrent); // Should fail

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        Assert.False(result3);
        Assert.Equal(1, limiter.GetCount("backend-a"));
        Assert.Equal(1, limiter.GetCount("backend-b"));
    }

    [Fact]
    public void GetCount_ReturnsZero_ForUnknownBackend()
    {
        // Arrange
        var limiter = new ConnectionLimiter();

        // Act
        var count = limiter.GetCount("nonexistent-backend");

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void TryAcquire_UnlimitedMode_AlwaysSucceeds()
    {
        // Arrange
        var limiter = new ConnectionLimiter();

        // Act
        var results = new bool[100];
        for (int i = 0; i < 100; i++)
            results[i] = limiter.TryAcquire("unlimited-backend", 0);

        // Assert
        Assert.All(results, r => Assert.True(r));
    }

    [Fact]
    public void Dispose_CleansUpResources()
    {
        // Arrange
        var limiter = new ConnectionLimiter();
        limiter.TryAcquire("test", 2);

        // Act
        limiter.Dispose();

        // Assert - should not throw
        Assert.Equal(0, limiter.GetCount("test"));
    }
}
