using PhoneBackup.Desktop;
using System.Windows;

namespace PhoneBackup.Desktop.Tests;

public sealed class OperationCancellationControllerTests
{
    [Fact]
    public void CancelSignalsActiveOperationAndCannotBeRequestedTwice()
    {
        using var controller = new OperationCancellationController();
        var token = controller.Begin();

        Assert.True(controller.IsActive);
        Assert.True(controller.CanCancel);
        Assert.True(controller.Cancel());
        Assert.True(token.IsCancellationRequested);
        Assert.True(controller.IsActive);
        Assert.False(controller.CanCancel);
        Assert.False(controller.Cancel());
    }

    [Fact]
    public void CompleteClearsOperationAndAllowsNextOne()
    {
        using var controller = new OperationCancellationController();
        controller.Begin();
        controller.Complete();

        Assert.False(controller.IsActive);
        Assert.False(controller.CanCancel);

        var next = controller.Begin();
        Assert.False(next.IsCancellationRequested);
        Assert.True(controller.CanCancel);
    }

    [Fact]
    public void ConcurrentOperationIsRejected()
    {
        using var controller = new OperationCancellationController();
        controller.Begin();

        Assert.Throws<InvalidOperationException>(() => controller.Begin());
    }

    [Fact]
    public async Task ViewModelCancelCommandCancelsBusyOperationAndResetsUiState()
    {
        var fakeAdbPath = Path.GetTempFileName();
        try
        {
            var viewModel = new MainViewModel(new AdbService(fakeAdbPath));
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = viewModel.BusyAsync(
                "copying",
                async cancellationToken =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                cancellable: true,
                cancellationStatus: "cancelled");

            await entered.Task;
            Assert.True(viewModel.IsBusy);
            Assert.True(viewModel.OperationProgressIndeterminate);
            Assert.True(viewModel.CanCancelOperation);
            Assert.Equal(Visibility.Visible, viewModel.CancelOperationVisibility);
            Assert.True(viewModel.CancelOperationCommand.CanExecute(null));

            viewModel.CancelOperationCommand.Execute(null);
            await operation;

            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.OperationProgressIndeterminate);
            Assert.False(viewModel.CanCancelOperation);
            Assert.Equal(Visibility.Collapsed, viewModel.CancelOperationVisibility);
            Assert.Equal("cancelled", viewModel.StatusText);
        }
        finally
        {
            File.Delete(fakeAdbPath);
        }
    }
}
