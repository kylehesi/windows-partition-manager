using System.Globalization;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Abstractions;

namespace WindowsPartitionManager.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void RestoreText_English_UsesIsoDate()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

            var vm = CreateViewModel('C');

            Assert.Contains("2026-10-10 22:14", vm.RestoreText);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void RestoreText_Turkish_UsesTurkishDate()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");

            var vm = CreateViewModel('C');

            Assert.Contains("10.10.2026 22:14", vm.RestoreText);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static SettingsViewModel CreateViewModel(char volume)
    {
        var state = new UnblockState
        {
            AppliedAt = new DateTimeOffset(2026, 10, 10, 22, 14, 0, TimeSpan.Zero),
            Volume = volume,
            HibernationWasEnabled = true,
        };
        return new SettingsViewModel("log", "state", new FakeUnblockOperations(state), _ => true);
    }

    private sealed class FakeUnblockOperations(UnblockState? state) : IUnblockOperations
    {
        public bool IsHibernationEnabled() => false;

        public Task SetHibernationAsync(bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public bool IsPageFileAutomatic() => false;

        public IReadOnlyList<PageFileSetting> GetPageFiles() => [];

        public Task<PageFileChange> RemovePageFileAsync(char volume, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RestorePageFileAsync(PageFileSetting? removed, bool automatic, char? temporaryVolume, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public int CountShadowCopies(char volume) => 0;

        public Task<int> DeleteShadowCopiesAsync(char volume, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public UnblockState? LoadState() => state;

        public void SaveState(UnblockState? state)
        {
        }
    }
}
