using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Imaging.Tests;

public class AsyncCommandTests
{
    [Fact]
    public async Task FailedStartNotificationRestoresAvailabilityAndAllowsRetry()
    {
        var executions = 0;
        var notifications = 0;
        var expected = new InvalidOperationException("알림 구독자 실패");
        var command = new AsyncCommand(() =>
        {
            executions++;
            return Task.CompletedTask;
        }, _ => Assert.Fail("ExecuteAsync는 예외를 호출자에게 전달해야 합니다."));
        command.CanExecuteChanged += (_, _) =>
        {
            if (++notifications == 1)
            {
                throw expected;
            }
        };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync());

        Assert.Same(expected, actual);
        Assert.Equal(0, executions);
        Assert.True(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, executions);
        Assert.True(command.CanExecute(null));
    }
}
