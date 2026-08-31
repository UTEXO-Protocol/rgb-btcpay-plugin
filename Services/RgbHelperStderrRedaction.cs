namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbHelperStderrRedaction
{
    public const string UploadedBackupFilePlaceholder = "<the backup file you uploaded>";

    public const string UploadDirectoryPlaceholder = "<the server upload directory>";

    public const string StagingDirectoryPlaceholder = "<the restore staging directory>";

    public const string WalletDataDirectoryPlaceholder = "<the wallet data directory>";

    public const string RestoreHelperAssemblyPlaceholder = "<the restore helper>";

    public const string PluginInstallDirectoryPlaceholder = "<the plugin install directory>";

    public const string NativeSendHelperAssemblyPlaceholder = "<the RGB send helper>";

    public const string WalletKeyedDataDirectoryPlaceholder = "<this wallet's data directory>";

    public const string WalletDataRootPlaceholder = "<the wallet data root>";

    public static string ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheRestoreHelper(
        string? childStdErr, string backupPath, string stagingDir, string? helperDll = null)
        => ReplaceEveryKnownHostPathLongestFirst(childStdErr, new[]
        {
            (Path: backupPath, Placeholder: UploadedBackupFilePlaceholder),
            (Path: stagingDir, Placeholder: StagingDirectoryPlaceholder),
            (Path: helperDll ?? string.Empty, Placeholder: RestoreHelperAssemblyPlaceholder),
            (Path: ContainingDirectoryOrEmpty(backupPath), Placeholder: UploadDirectoryPlaceholder),
            (Path: ContainingDirectoryOrEmpty(stagingDir), Placeholder: WalletDataDirectoryPlaceholder),
            (Path: ContainingDirectoryOrEmpty(helperDll), Placeholder: PluginInstallDirectoryPlaceholder)
        });

    public static string ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheNativeSendHelper(
        string? childStdErr, string walletDataDir, string leaseWalletDir, string? helperDll = null)
        => ReplaceEveryKnownHostPathLongestFirst(childStdErr, new[]
        {
            (Path: leaseWalletDir, Placeholder: WalletKeyedDataDirectoryPlaceholder),
            (Path: walletDataDir, Placeholder: WalletDataDirectoryPlaceholder),
            (Path: helperDll ?? string.Empty, Placeholder: NativeSendHelperAssemblyPlaceholder),
            (Path: ContainingDirectoryOrEmpty(walletDataDir), Placeholder: WalletDataRootPlaceholder),
            (Path: ContainingDirectoryOrEmpty(helperDll), Placeholder: PluginInstallDirectoryPlaceholder)
        });

    static string ReplaceEveryKnownHostPathLongestFirst(
        string? childStdErr, IEnumerable<(string Path, string Placeholder)> candidates)
    {
        var text = childStdErr ?? string.Empty;
        foreach (var known in KnownAbsolutePathsLongestFirst(candidates))
            text = text.Replace(known.Path, known.Placeholder, StringComparison.Ordinal);
        return text;
    }

    static IEnumerable<(string Path, string Placeholder)> KnownAbsolutePathsLongestFirst(
        IEnumerable<(string Path, string Placeholder)> candidates)
        => candidates
            .Where(candidate =>
                NamesAHostLocationRatherThanAFragmentThatCouldMangleTheDiagnostic(candidate.Path))
            .OrderByDescending(candidate => candidate.Path.Length)
            .ToList();

    static string ContainingDirectoryOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetDirectoryName(path) ?? string.Empty; }
        catch (ArgumentException) { return string.Empty; }
    }

    static bool NamesAHostLocationRatherThanAFragmentThatCouldMangleTheDiagnostic(string path)
        => !string.IsNullOrWhiteSpace(path)
            && Path.IsPathFullyQualified(path)
            && ContainingDirectoryOrEmpty(path).Length > 0;
}
