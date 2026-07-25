using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Sonulab.Distill;
using Xunit;

public class CatalogVersionTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"catalog-vm-backups-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true); }

    /// <summary>A realistic device slot: constant vxamp header, fill-byte body, ZERO padding
    /// (like every real VoidX-written slot). The metadata-save integrity guards reject blobs
    /// without the header, so fixtures must carry it. Mirrors AmpListViewModelTests.RealisticBlob.</summary>
    private static byte[] RealisticBlob(byte fill)
    {
        var blob = Enumerable.Repeat(fill, 12288).ToArray();
        VxampFormat.HeaderBytes.CopyTo(blob, 0);
        Array.Clear(blob, VxampMetadata.Offset, 12288 - VxampMetadata.Offset);
        return blob;
    }

    private (AmpListViewModel vm, FakeAmpDevice dev, CatalogVersion catalog) MakeAmps(bool writes = true)
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", RealisticBlob(1));
        dev.SeedAmp(1, "Crunch", RealisticBlob(2));
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var catalog = new CatalogVersion();
        return (new AmpListViewModel(svc, writes, catalog: catalog), dev, catalog);
    }

    private (IrListViewModel vm, FakeIrDevice dev, CatalogVersion catalog) MakeIrs(bool writes = true)
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(0, "Ir0", Enumerable.Repeat((byte)1, 4096).ToArray());
        dev.SeedIr(1, "Ir1", Enumerable.Repeat((byte)2, 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var catalog = new CatalogVersion();
        return (new IrListViewModel(svc, writes, catalog: catalog), dev, catalog);
    }

    [Fact] public void Bump_increments_monotonically()
    {
        var c = new CatalogVersion();
        int start = c.Version;
        c.Bump(); c.Bump();
        Assert.Equal(start + 2, c.Version);
    }

    [Fact] public async Task Deleting_an_amp_bumps_the_catalog()
    {
        // Build the VM exactly as AmpListViewModelTests does, additionally passing `catalog`.
        var (vm, _, catalog) = MakeAmps();
        await vm.RefreshCommand.ExecuteAsync(null);
        int before = catalog.Version;
        vm.Selected = vm.Items.First(i => !i.IsEmpty);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.True(catalog.Version > before);
    }

    [Fact] public async Task Refreshing_amps_does_not_bump_the_catalog()
    {
        var (vm, _, catalog) = MakeAmps();
        await vm.RefreshCommand.ExecuteAsync(null);
        int before = catalog.Version;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(before, catalog.Version);
    }

    [Fact] public async Task A_failed_amp_delete_does_not_bump_the_catalog()
    {
        // Construct the VM with writesAllowed: false — RunAsync returns before doing any work.
        var (vm, _, catalog) = MakeAmps(writes: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        int before = catalog.Version;
        vm.Selected = vm.Items.First(i => !i.IsEmpty);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(before, catalog.Version);
    }

    [Fact] public async Task Deleting_an_IR_bumps_the_catalog()
    {
        var (vm, _, catalog) = MakeIrs();
        await vm.RefreshCommand.ExecuteAsync(null);
        int before = catalog.Version;
        vm.Selected = vm.Items.First(i => !i.IsEmpty);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.True(catalog.Version > before);
    }
}
