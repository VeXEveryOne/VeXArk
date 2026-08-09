using PhoneBackup.Core;
using PhoneBackup.Desktop;

namespace PhoneBackup.Desktop.Tests;

public sealed class RestoreReportTests
{
    [Fact]
    public void FormatRestoreReportCollapsesSafeItems()
    {
        var items = Enumerable.Range(1, 304)
            .Select(index => new CompatibilityItem(
                $"app.{index}",
                CompatibilityLevel.Safe,
                "Подпись приложения совпадает."))
            .ToList();
        var report = new CompatibilityReport(CompatibilityLevel.Safe, items);

        var text = MainViewModel.FormatRestoreReport(report);

        Assert.Contains("без проблем — 304", text);
        Assert.Contains("Safe: 304 компонентов", text);
        Assert.DoesNotContain("app.1 —", text);
        Assert.True(text.Split(Environment.NewLine).Length <= 2);
    }

    [Fact]
    public void FormatRestoreReportKeepsItemsThatNeedAttention()
    {
        var report = new CompatibilityReport(
            CompatibilityLevel.Blocked,
            [
                new("app.safe", CompatibilityLevel.Safe, "OK"),
                new("app.old", CompatibilityLevel.Conditional, "Нужен downgrade."),
                new("app.bad", CompatibilityLevel.Blocked, "Подпись не совпала.")
            ]);

        var text = MainViewModel.FormatRestoreReport(report);

        Assert.Contains("Conditional: app.old — Нужен downgrade.", text);
        Assert.Contains("Blocked: app.bad — Подпись не совпала.", text);
        Assert.DoesNotContain("app.safe —", text);
    }
}
