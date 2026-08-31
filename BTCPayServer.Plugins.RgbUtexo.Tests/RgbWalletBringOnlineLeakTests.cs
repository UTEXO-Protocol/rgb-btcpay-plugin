using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbWalletBringOnlineLeakTests
{
    sealed class FakeNativeWallet
    {
        internal int DisposeCalls;
        internal bool DisposeThrows;

        internal void Dispose()
        {
            DisposeCalls++;
            if (DisposeThrows) throw new IOException("native free failed");
        }
    }

    sealed class FakeHandle
    {
        internal FakeNativeWallet Wallet { get; init; } = null!;
    }

    static FakeHandle Run(
        FakeNativeWallet wallet,
        Action<FakeNativeWallet> bringOnline,
        Func<FakeNativeWallet, FakeHandle>? buildHandle = null,
        List<Exception>? disposeFailures = null)
        => RgbLibService.CreateHandleOrDisposeWallet(
            wallet,
            bringOnline,
            buildHandle ?? (w => new FakeHandle { Wallet = w }),
            w => w.Dispose(),
            error => disposeFailures?.Add(error));

    [Fact]
    public void ABringOnlineFailureDisposesTheNativeWalletSoItsRuntimeLockDoesNotOutliveIt()
    {
        var wallet = new FakeNativeWallet();

        var thrown = Assert.Throws<TimeoutException>(() =>
            Run(wallet, _ => throw new TimeoutException("electrum unreachable")));

        Assert.Equal("electrum unreachable", thrown.Message);
        Assert.Equal(1, wallet.DisposeCalls);
    }

    [Fact]
    public void AHandleConstructionFailureAlsoDisposesTheNativeWallet()
    {
        var wallet = new FakeNativeWallet();

        Assert.Throws<InvalidOperationException>(() =>
            Run(wallet, _ => { }, _ => throw new InvalidOperationException("handle rejected")));

        Assert.Equal(1, wallet.DisposeCalls);
    }

    [Fact]
    public void ADisposeFailureIsReportedButNeverMasksTheOriginalFailure()
    {
        var wallet = new FakeNativeWallet { DisposeThrows = true };
        var disposeFailures = new List<Exception>();

        var thrown = Assert.Throws<TimeoutException>(() =>
            Run(wallet, _ => throw new TimeoutException("electrum unreachable"), null, disposeFailures));

        Assert.Equal("electrum unreachable", thrown.Message);
        Assert.Single(disposeFailures);
        Assert.IsType<IOException>(disposeFailures[0]);
    }

    [Fact]
    public void ASuccessfulBringOnlineReturnsTheHandleAndDisposesNothing()
    {
        var wallet = new FakeNativeWallet();

        var handle = Run(wallet, _ => { });

        Assert.Same(wallet, handle.Wallet);
        Assert.Equal(0, wallet.DisposeCalls);
    }

    [Fact]
    public void CreateWalletInternalRoutesBringOnlineThroughTheDisposingHelperRatherThanCallingItDirectly()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var model = plugin.Model(tree);
        var method = RoslynPins.Method(tree, "RgbLibService", "CreateWalletInternal");
        var body = RoslynPins.BodyOf(method);

        var helperCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "CreateHandleOrDisposeWallet",
                ContainingType.Name: "RgbLibService"
            })
            .ToList();
        Assert.True(helperCalls.Count == 1,
            $"CreateWalletInternal must reach the native wallet online through the disposing helper exactly "
            + $"once, found {helperCalls.Count} — a direct GoOnline leaks a live rgb-lib wallet and its "
            + "rgb_runtime.lock when the indexer is unreachable");

        var goOnlineCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "GoOnline"
            })
            .ToList();
        Assert.True(goOnlineCalls.Count == 1,
            $"expected exactly one GoOnline call site, found {goOnlineCalls.Count}");
        Assert.True(goOnlineCalls[0].Ancestors().Contains(helperCalls[0]),
            "GoOnline must be invoked inside the disposing helper's argument, otherwise a connect failure "
            + "still abandons a live native wallet");
    }
}
