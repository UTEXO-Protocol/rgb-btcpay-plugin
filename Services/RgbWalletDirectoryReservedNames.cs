namespace BTCPayServer.Plugins.RgbUtexo.Services;

internal static class RgbWalletDirectoryReservedNames
{
    internal const string PinnedRgbLibBeta30NonWatchOnlyBdkStoreFileName = "bdk_db";

    internal const string PinnedRgbLibBeta30BdkStoreRepairTempSuffixReReadLoadOrRecoverBdkStoreWhenBumpingRgbLib =
        ".recovering";

    internal const string PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName =
        RgbStockDurability.WatchOnlyBdkStoreFileName
        + PinnedRgbLibBeta30BdkStoreRepairTempSuffixReReadLoadOrRecoverBdkStoreWhenBumpingRgbLib;

    internal const string PinnedRgbLibBeta30NonWatchOnlyBdkStoreRepairTempFileName =
        PinnedRgbLibBeta30NonWatchOnlyBdkStoreFileName
        + PinnedRgbLibBeta30BdkStoreRepairTempSuffixReReadLoadOrRecoverBdkStoreWhenBumpingRgbLib;

    internal const string PinnedRgbLibBeta30SendConsignmentFileNameReReadGenConsignmentsWhenBumpingRgbLib =
        "consignment_out";

    internal const string PinnedRgbLibBeta30TransfersDirectoryNameReReadTransfersDirWhenBumpingRgbLib =
        "transfers";

    internal static readonly IReadOnlyList<string> NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant =
    [
        PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName,
        PinnedRgbLibBeta30NonWatchOnlyBdkStoreRepairTempFileName
    ];

    internal static readonly IReadOnlyList<string> NamesWrittenByThePinnedRgbLibsSendEndConsignmentPathAndHavingNoManagedWriter =
    [
        PinnedRgbLibBeta30SendConsignmentFileNameReReadGenConsignmentsWhenBumpingRgbLib
    ];

    internal static readonly IReadOnlyList<string> NamesThatMustBeRegularFilesNotDirectories =
    [
        RgbNativeSendLease.ParentFileName,
        RgbNativeSendLease.WorkerFileName,
        RgbNativeSendLease.WalletAccessFileName,
        RgbNativeSendLease.RgbRuntimeLockFileName,
        RgbSendRecoveryJournal.FileName,
        RgbSendRecoveryJournal.TransferFasciaFileName,
        RgbSendRecoveryJournal.TransferSignedPsbtFileName,
        .. RgbStockDurability.StockFiles,
        .. NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant,
        .. NamesWrittenByThePinnedRgbLibsSendEndConsignmentPathAndHavingNoManagedWriter
    ];

    internal static readonly IReadOnlyList<string>
        NamesCreatedAsDirectoriesByThePinnedRgbLibOnlyOnPathsReachedAfterTheRestoreIsAlreadyPublished =
    [
        PinnedRgbLibBeta30TransfersDirectoryNameReReadTransfersDirWhenBumpingRgbLib
    ];

    internal static readonly IReadOnlyList<string> NamesThatMustBeDirectoriesNotRegularFiles =
    [
        .. NamesCreatedAsDirectoriesByThePinnedRgbLibOnlyOnPathsReachedAfterTheRestoreIsAlreadyPublished
    ];
}
