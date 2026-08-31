using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbModelSnapshotDriftTests
{
    const string ModelOnlyConnectionStringThatIsNeverDialled =
        "Host=127.0.0.1;Port=1;Database=model_only;Username=none;Password=none";

    static RGBPluginDbContext CreateModelOnlyContext()
    {
        var factory = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = ModelOnlyConnectionStringThatIsNeverDialled
        }));
        return factory.CreateContext();
    }

    [Fact]
    public void CommittedModelSnapshot_StillDescribesTheEntityModelExactly_SoNoHandWrittenMigrationLeavesItStale()
    {
        using var ctx = CreateModelOnlyContext();

        var snapshot = ctx.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.True(snapshot != null,
            "the plugin ships a committed RGBPluginDbContextModelSnapshot and the migrations assembly must be able "
            + "to find it, otherwise every future 'dotnet ef migrations add' rebuilds the whole schema from nothing");

        var snapshotModel = snapshot!.Model;
        if (snapshotModel is IMutableModel mutableSnapshotModel)
            snapshotModel = mutableSnapshotModel.FinalizeModel();
        snapshotModel = ctx.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshotModel, designTime: true, validationLogger: null);

        var pendingOperations = ctx.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            ctx.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(pendingOperations.Count == 0,
            "the committed model snapshot has drifted from the entity model, so the next hand-written migration will "
            + "be diffed against a stale baseline and will either repeat or omit schema changes. "
            + "RGBPluginDbContext suppresses PendingModelChangesWarning, so nothing at runtime reports this. "
            + "Regenerate the snapshot with 'dotnet ef migrations add' and discard the empty migration it also emits. "
            + "Drift detected as: "
            + string.Join(", ", pendingOperations.Select(operation => operation.GetType().Name)));
    }

    [Fact]
    public void SnapshotDeclaresIssuedSupplyAsTheConvertersProviderType_NotTheEntitysUnsignedType()
    {
        using var ctx = CreateModelOnlyContext();

        var issuedSupply = ctx.Model
            .FindEntityType(typeof(Data.Entities.RGBAsset))!
            .FindProperty(nameof(Data.Entities.RGBAsset.IssuedSupply))!;

        Assert.Equal(typeof(ulong), issuedSupply.ClrType);
        Assert.Equal(typeof(long), issuedSupply.GetValueConverter()!.ProviderClrType);
        Assert.Equal("bigint", issuedSupply.GetColumnType());
    }
}
