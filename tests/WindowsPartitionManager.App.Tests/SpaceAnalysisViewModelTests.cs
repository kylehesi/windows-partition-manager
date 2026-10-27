using System.Globalization;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;

namespace WindowsPartitionManager.App.Tests;

public sealed class SpaceAnalysisViewModelTests
{
    private static readonly SpaceEntry[] Entries =
    [
        new(5, 5, ".", true, 0),
        new(10, 5, "Games", true, 0),
        new(11, 10, "Big", true, 0),
        new(20, 11, "game.pak", false, 50_000),
        new(21, 10, "readme.txt", false, 1_000),
        new(22, 5, "pagefile.sys", false, 8_000),
    ];

    private static async Task<SpaceAnalysisViewModel> LoadedAsync()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var vm = new SpaceAnalysisViewModel(new FixedReader(), 'd');
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task Root_ListsFoldersAndFiles_LargestFirst_WithShares()
    {
        var vm = await LoadedAsync();

        Assert.False(vm.IsLoading);
        Assert.Equal(@"D:\", vm.Location);
        Assert.Equal(["Games", "pagefile.sys"], vm.Items.Select(i => i.Name));
        Assert.True(vm.Items[0].IsFolder);
        Assert.Equal(51_000.0 / 59_000, vm.Items[0].Fraction, precision: 6);
        Assert.False(vm.UpCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpeningAFolder_AndGoingUp_FollowTheTree()
    {
        var vm = await LoadedAsync();

        vm.OpenCommand.Execute(vm.Items[0]);
        Assert.Equal(@"D:\Games", vm.Location);
        Assert.Equal(["Big", "readme.txt"], vm.Items.Select(i => i.Name));
        Assert.True(vm.UpCommand.CanExecute(null));

        vm.UpCommand.Execute(null);
        Assert.Equal(@"D:\", vm.Location);
    }

    [Fact]
    public async Task LargestFiles_ListTheWholeDrive_WithTheirFolders()
    {
        var vm = await LoadedAsync();

        vm.ShowLargestFiles = true;

        Assert.Equal("game.pak", vm.Items[0].Name);
        Assert.Equal(@"D:\Games\Big", vm.Items[0].Detail);
        Assert.Equal(@"D:\Games\Big\game.pak", vm.Items[0].FullPath);
        Assert.False(vm.UpCommand.CanExecute(null));
    }

    [Fact]
    public async Task AReadError_IsShown_NotThrown()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var vm = new SpaceAnalysisViewModel(new FailingReader(), 'C');

        await vm.LoadAsync();

        Assert.True(vm.HasError);
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Items);
    }

    private sealed class FixedReader : IVolumeSpaceReader
    {
        public Task<IReadOnlyList<SpaceEntry>> ReadAsync(char driveLetter, IProgress<InspectProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpaceEntry>>(Entries);
    }

    private sealed class FailingReader : IVolumeSpaceReader
    {
        public Task<IReadOnlyList<SpaceEntry>> ReadAsync(char driveLetter, IProgress<InspectProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("Opening volume C: requires administrator rights.");
    }
}
