using System.Runtime.InteropServices;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public sealed class RgbNativeCandidateLoopTests : IDisposable
{
    readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // Expectations are written out literally here — never obtained from RuntimeIdentifiers() or
    // NativeFileName() — so a body-only mutant in either helper cannot restate itself into a pass.
    [Fact]
    public void CandidatePaths_DedupesAndPreservesProbeOrder()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "rgb-candidate-base");

        var fileName = OperatingSystem.IsWindows() ? "rgbverifycffi.dll"
            : OperatingSystem.IsMacOS() ? "librgbverifycffi.dylib"
            : "librgbverifycffi.so";

        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        var rids = new List<string> { RuntimeInformation.RuntimeIdentifier };
        if (!rids.Contains($"{os}-{arch}")) rids.Add($"{os}-{arch}");

        var expected = rids
            .Select(rid => Path.Combine(baseDir, "runtimes", rid, "native", fileName))
            .Append(Path.Combine(baseDir, fileName))
            .ToList();

        var actual = RgbVerifyNative.CandidatePaths(baseDir).ToList();

        Assert.Equal(expected, actual);
        Assert.Equal(actual.Count, actual.Distinct().Count());
        Assert.All(actual, path => Assert.EndsWith(fileName, path));
    }

    [Fact]
    public void TryLoadFromCandidates_NothingPresent_ReportsEveryCandidateAndLoadsNothing()
    {
        var baseDir = TempDir();
        var loader = new RecordingLoader();

        var loaded = RgbVerifyNative.TryLoadFromCandidates(baseDir, out var handle, out var winningPath,
            out var searched, out var existedButFailed, loader.Load);

        Assert.False(loaded);
        Assert.Equal(IntPtr.Zero, handle);
        Assert.Null(winningPath);
        Assert.Equal(RgbVerifyNative.CandidatePaths(baseDir).ToList(), searched);
        Assert.Empty(existedButFailed);
        Assert.Empty(loader.Calls);
    }

    [Fact]
    public void TryLoadFromCandidates_PresentButUnloadable_ReportsItAsExistedButFailed()
    {
        var baseDir = TempDir();
        var candidates = RgbVerifyNative.CandidatePaths(baseDir).ToList();
        Plant(candidates[0]);

        var loader = new RecordingLoader();

        var loaded = RgbVerifyNative.TryLoadFromCandidates(baseDir, out var handle, out var winningPath,
            out var searched, out var existedButFailed, loader.Load);

        Assert.False(loaded);
        Assert.Equal(IntPtr.Zero, handle);
        Assert.Null(winningPath);
        Assert.Equal(candidates, searched);
        Assert.Equal(new[] { candidates[0] }, existedButFailed);
        Assert.Equal(new[] { candidates[0] }, loader.Calls);
    }

    // The recorded-call list is what distinguishes a first-wins loop from one that loads every
    // present candidate and returns the first handle: the latter dlopens images the probe never
    // needed, widening the initializer-abort radius to every candidate on disk.
    [Fact]
    public void TryLoadFromCandidates_FirstCandidateWins_AndStopsThere()
    {
        var baseDir = TempDir();
        var candidates = RgbVerifyNative.CandidatePaths(baseDir).ToList();
        Assert.True(candidates.Count >= 2, "this host yields fewer than two candidate paths");
        Plant(candidates[0]);
        Plant(candidates[1]);

        var loader = new RecordingLoader();
        loader.Handles[candidates[0]] = (IntPtr)11;
        loader.Handles[candidates[1]] = (IntPtr)22;

        var loaded = RgbVerifyNative.TryLoadFromCandidates(baseDir, out var handle, out var winningPath,
            out var searched, out var existedButFailed, loader.Load);

        Assert.True(loaded);
        Assert.Equal((IntPtr)11, handle);
        Assert.Equal(candidates[0], winningPath);
        Assert.Equal(new[] { candidates[0] }, searched);
        Assert.Empty(existedButFailed);
        Assert.Equal(new[] { candidates[0] }, loader.Calls);
    }

    [Fact]
    public void TryLoadFromCandidates_SecondCandidateWins_NamesTheSecondPath()
    {
        var baseDir = TempDir();
        var candidates = RgbVerifyNative.CandidatePaths(baseDir).ToList();
        Assert.True(candidates.Count >= 2, "this host yields fewer than two candidate paths");
        Plant(candidates[1]);

        var loader = new RecordingLoader();
        loader.Handles[candidates[1]] = (IntPtr)33;

        var loaded = RgbVerifyNative.TryLoadFromCandidates(baseDir, out var handle, out var winningPath,
            out var searched, out var existedButFailed, loader.Load);

        Assert.True(loaded);
        Assert.Equal((IntPtr)33, handle);
        Assert.Equal(searched[1], winningPath);
        Assert.Equal(candidates[1], winningPath);
        Assert.Empty(existedButFailed);
        Assert.Equal(new[] { candidates[1] }, loader.Calls);
    }

    string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rgb-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    static void Plant(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a real native library");
    }

    sealed class RecordingLoader
    {
        internal List<string> Calls { get; } = [];
        internal Dictionary<string, IntPtr> Handles { get; } = [];

        internal IntPtr Load(string path)
        {
            Calls.Add(path);
            return Handles.TryGetValue(path, out var handle) ? handle : IntPtr.Zero;
        }
    }
}
