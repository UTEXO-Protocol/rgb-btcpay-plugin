using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSendClosureSourceTests
{
    static string RepoRoot() => System.Reflection.CustomAttributeExtensions
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(RgbSendClosureSourceTests).Assembly)
        .Single(a => a.Key == "RepoRoot").Value!;

    static string Source()
    {
        return File.ReadAllText(Path.Combine(RepoRoot(), "Services", "RGBWalletService.cs"));
    }

    [Fact]
    public void EndpointNativeCallsAreOnlyMadeThroughTheKillableWorker()
    {
        var source = Source();
        Assert.DoesNotContain("_rgbLib.SendBeginAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_rgbLib.SendEndAsync(", source, StringComparison.Ordinal);
        Assert.Contains("RunNativeSendIsolatedAsync(\n                wallet, \"send-begin\"", source, StringComparison.Ordinal);
        Assert.Contains("RunNativeSendIsolatedAsync(\n                    wallet, \"send-end\"", source, StringComparison.Ordinal);
        Assert.Contains("if (!result.ChildReaped)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPostSendBeginOrdinaryFailureRunsDurableReconciliation()
    {
        var source = Source();
        Assert.Contains("sendBeginMayHaveRun && !sendEndStarted", source, StringComparison.Ordinal);
        Assert.Contains("await ReconcileWalletRecoveryAsync(wallet, CancellationToken.None, operationLease)", source,
            StringComparison.Ordinal);
        Assert.Contains("RgbSendRecoveryPhase.SendEndIndeterminate", source, StringComparison.Ordinal);
        Assert.Contains("durable quarantine retained", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactTransactionIsDurableBeforeSendEndAndAckGatesBroadcast()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var send = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync"));

        var write = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "WriteSendEnd",
                ContainingType.Name: "RgbSendRecoveryJournal"
            });
        Assert.Contains("batchTransferIdx", write.ArgumentList.ToString(), StringComparison.Ordinal);
        Assert.Contains("rawTransactionHex", write.ArgumentList.ToString(), StringComparison.Ordinal);
        Assert.Contains("txid", write.ArgumentList.ToString(), StringComparison.Ordinal);
        Assert.Contains("signedPsbt", write.ArgumentList.ToString(), StringComparison.Ordinal);

        var sendEnd = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RunNativeSendIsolatedAsync",
                ContainingType.Name: "RGBWalletService"
            } && i.ArgumentList.Arguments.Any(a => a.Expression.ToString() == "\"send-end\""));
        var preSendEndDurability = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "FsyncPreSendEndArtifacts",
                ContainingType.Name: "RgbSendRecoveryJournal"
            });
        var ackArtifactDurability = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RestoreAndFsyncAckBroadcastArtifacts",
                ContainingType.Name: "RgbSendRecoveryJournal"
            });
        Assert.True(write.SpanStart < preSendEndDurability.SpanStart
                    && preSendEndDurability.SpanStart < sendEnd.SpanStart
                    && sendEnd.SpanStart < ackArtifactDurability.SpanStart);

        var delete = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "Delete",
                ContainingType.Name: "RgbSendRecoveryJournal"
            });
        var refresh = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol { Name: "RefreshAsync" });
        Assert.True(sendEnd.SpanStart < refresh.SpanStart && refresh.SpanStart < delete.SpanStart);
        Assert.True(ackArtifactDurability.SpanStart < refresh.SpanStart);
        var journalSource = File.ReadAllText(Path.Combine(
            RepoRoot(), "Services", "RgbSendRecoveryJournal.cs"));
        var preArtifactStart = journalSource.IndexOf(
            "internal static void FsyncPreSendEndArtifacts", StringComparison.Ordinal);
        var restoreArtifactStart = journalSource.IndexOf(
            "internal static void RestoreAndFsyncAckBroadcastArtifacts", StringComparison.Ordinal);
        var preArtifactMethod = journalSource[preArtifactStart..restoreArtifactStart];
        Assert.Contains("FlushDirectory(transferDir)", preArtifactMethod, StringComparison.Ordinal);
        Assert.Contains("FlushDirectory(transfersDir)", preArtifactMethod, StringComparison.Ordinal);
        Assert.Contains("FlushDirectory(walletDir)", preArtifactMethod, StringComparison.Ordinal);
        Assert.DoesNotContain(send.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "EnsureRecoveryTransactionBroadcastAsync",
                ContainingType.Name: "RGBWalletService"
            });

        var reconcile = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "ReconcileWalletRecoveryAsync"));
        Assert.Equal(5, reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "FindOutgoingBatchStatusAsync",
                ContainingType.Name: "RGBWalletService"
            }));
        var ackDurabilityDecisions = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ShouldQuarantineIncompleteAckRecovery",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.True(ackDurabilityDecisions == 2,
            $"exactly two ACK-durability decisions are mandated, found {ackDurabilityDecisions}: the "
            + "phase-only journal's, and the unparseable journal reusing that same decision so it can "
            + "never be discharged on weaker evidence than a journal that parses. A third is an "
            + "unreviewed quarantine rule; one means an unparseable journal decides for itself.");
        Assert.Single(reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "EnsureRecoveryTransactionBroadcastAsync",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.Single(reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ShouldRebroadcastRecoveredTransaction",
                ContainingType.Name: "RGBWalletService"
            });

        var replay = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RunNativeSendIsolatedAsync",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.Contains(replay.Ancestors().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString() == "status == RgbLibTransferStatusInitiated");
        Assert.Contains("recovery.SignedPsbt", replay.ArgumentList.ToString(), StringComparison.Ordinal);

        var prepareReplay = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "PrepareWorkerReplay",
                ContainingType.Name: "RgbNativeSendLease"
            });
        var reclaimReplay = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ReclaimWorkerAfterReplay",
                ContainingType.Name: "RgbNativeSendLease"
            });
        Assert.True(prepareReplay.SpanStart < replay.SpanStart);
        Assert.True(replay.SpanStart < reclaimReplay.SpanStart);

        var reapedFallback = reconcile.DescendantNodes().OfType<CatchClauseSyntax>()
            .Single(c => c.Declaration?.Type.ToString() == "NativeSendReapedFailureException");
        Assert.Contains("replayFailedAfterReap = true", reapedFallback.Block.ToString(),
            StringComparison.Ordinal);
        var failAfterReap = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ShouldFailInitiatedAfterReapedReplayFailure",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.True(reapedFallback.SpanStart < failAfterReap.SpanStart);

        var replayValidation = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ValidateRecoveryPsbt",
                ContainingType.Name: "RGBWalletService"
            } && i.Ancestors().OfType<IfStatementSyntax>().Any(
                a => a.Condition.ToString() == "status == RgbLibTransferStatusInitiated"));
        Assert.True(replayValidation.SpanStart < replay.SpanStart);
        var recoveryArtifactDurability = reconcile.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RestoreAndFsyncAckBroadcastArtifacts",
                ContainingType.Name: "RgbSendRecoveryJournal"
            }).ToList();
        Assert.Equal(2, recoveryArtifactDurability.Count);
        var phaseRecovery = reconcile.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString()
                == "phase == RgbSendRecoveryPhase.SendEndIndeterminate");
        var reconcileRefreshes = phaseRecovery.Statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol { Name: "RefreshAsync" })
            .OrderBy(i => i.SpanStart).ToList();
        Assert.True(recoveryArtifactDurability[0].SpanStart < reconcileRefreshes[0].SpanStart);
        Assert.True(replay.SpanStart < recoveryArtifactDurability[1].SpanStart);
        Assert.Contains(reconcileRefreshes,
            refreshAfterReplay => recoveryArtifactDurability[1].SpanStart < refreshAfterReplay.SpanStart);

        var orphanQueries = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "FindOrphanedOutgoingBatchIndicesAsync",
                ContainingType.Name: "RGBWalletService"
            }).ToList();
        var failTransfer = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "FailTransfersAsync"
            });
        Assert.True(failAfterReap.SpanStart < failTransfer.SpanStart);
        Assert.Contains(orphanQueries, query => replay.SpanStart < query.SpanStart
                                                && query.SpanStart < failTransfer.SpanStart);

        Assert.DoesNotContain(reconcile.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Statement.ToString().Contains(
                "legacy send_end recovery", StringComparison.Ordinal));
        var drain = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "DrainOrphanedOutgoingBatchesAsync",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.True(drain.SpanStart <= failTransfer.SpanStart);
    }

    [Fact]
    public void OneOperationLeaseEnclosesBothNativeHelperPhases()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var method = RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync");
        var body = RoslynPins.BodyOf(method);
        var model = plugin.Model(tree);

        var acquire = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "AcquireNativeSendParentLease",
                ContainingType.Name: "RGBWalletService"
            });
        var usingDeclaration = acquire.Ancestors().OfType<LocalDeclarationStatementSyntax>()
            .SingleOrDefault();
        Assert.NotNull(usingDeclaration);
        Assert.False(usingDeclaration!.UsingKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None));

        var wrapper = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "AcquireNativeSendParentLease"));
        Assert.Single(wrapper.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "AcquireParent",
                ContainingType.Name: "RgbNativeSendLease"
            });

        var helperCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RunNativeSendIsolatedAsync",
                ContainingType.Name: "RGBWalletService"
            }).ToList();
        Assert.Equal(2, helperCalls.Count);
        Assert.All(helperCalls, call => Assert.True(acquire.SpanStart < call.SpanStart));

        var clear = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ClearActiveMarker",
                ContainingType.Name: "RgbNativeSendLease"
            });
        Assert.NotNull(clear.Ancestors().OfType<FinallyClauseSyntax>().SingleOrDefault());
        Assert.All(helperCalls, call => Assert.True(call.SpanStart < clear.SpanStart));
    }

    [Fact]
    public void PreexistingQuarantineIsRejectedWithoutRejectingThisSendsWriteAhead()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var send = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync"));
        var quarantineChecks = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "IsNeedsRecoveryAsync",
                ContainingType.Name: "RGBWalletService"
            }).ToList();
        Assert.Equal(2, quarantineChecks.Count);
        var writeAhead = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "SetNeedsRecoveryAsync",
                ContainingType.Name: "RGBWalletService"
            });
        var acquire = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "AcquireNativeSendParentLease",
                ContainingType.Name: "RGBWalletService"
            });
        var snapshot = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "SnapshotVerificationStateAsync",
                ContainingType.Name: "IRgbLibService"
            });
        Assert.True(quarantineChecks[0].SpanStart < acquire.SpanStart);
        Assert.True(acquire.SpanStart < quarantineChecks[1].SpanStart);
        Assert.True(quarantineChecks[1].SpanStart < snapshot.SpanStart);
        Assert.True(snapshot.SpanStart < writeAhead.SpanStart);
        var protectedAdmission = quarantineChecks[1].Ancestors().OfType<IfStatementSyntax>().Single();
        Assert.Contains(protectedAdmission.Condition.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "Exists",
                ContainingType.Name: "File"
            });
        var guardedScope = quarantineChecks[1].Ancestors().OfType<TryStatementSyntax>().First();
        Assert.NotNull(guardedScope.Finally);
        Assert.Contains(guardedScope.Finally!.DescendantNodes()
                .OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ClearActiveMarker",
                ContainingType.Name: "RgbNativeSendLease"
            });

        var gate = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "RunIntentGateAsync"));
        Assert.DoesNotContain(gate.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "IsNeedsRecoveryAsync",
                ContainingType.Name: "RGBWalletService"
            });
    }

    [Fact]
    public void HealthyRefreshDoesNotPublishAHelperMarker()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var refresh = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "RefreshWalletAsync"));
        var reconcileCall = refresh.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ReconcileWalletRecoveryAsync",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.Contains(reconcileCall.ArgumentList.Arguments,
            a => a.NameColon?.Name.Identifier.ValueText == "durableRecoveryWasPending"
                 && a.Expression is PrefixUnaryExpressionSyntax
                 {
                     RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalNotExpression
                 });

        var reconcile = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "ReconcileWalletRecoveryAsync"));
        var acquireRecovery = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "AcquireNativeSendRecoveryLease",
                ContainingType.Name: "RGBWalletService"
            });
        var healthyReturn = reconcile.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Single(r => r.Ancestors().OfType<IfStatementSyntax>().Any(i =>
                i.Condition.ToString().Contains("probe.Count == 0", StringComparison.Ordinal)));
        Assert.True(healthyReturn.SpanStart < acquireRecovery.SpanStart);
        Assert.Contains(healthyReturn.Ancestors().OfType<IfStatementSyntax>().First()
                .Statement.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RefreshAsync",
                ContainingType.Name: "IRgbLibService"
            });

    }

    [Fact]
    public void MarkerOnlyRecoveryFailureRemovesItsSyntheticMarker()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var reconcile = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "ReconcileWalletRecoveryAsync"));
        var markerOnlyClear = reconcile.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!completed && markerOnlyProven");
        var markerOnlyAssignment = reconcile.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "markerOnlyProven");
        Assert.Equal("phase == null && orphans.Count == 0",
            markerOnlyAssignment.Right.ToString());
        Assert.Contains(markerOnlyClear.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ClearActiveMarker",
                ContainingType.Name: "RgbNativeSendLease"
            });
    }

    [Fact]
    public void EveryCachedNativeOperationAcquiresWalletAccessLease()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibWalletHandle.cs");
        var model = plugin.Model(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "ExecuteAsync").ToList();

        Assert.Equal(2, methods.Count);
        Assert.All(methods, method =>
        {
            var acquisitions = RoslynPins.BodyOf(method).DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
                {
                    Name: "AcquireWalletAccess",
                    ContainingType.Name: "RgbNativeSendLease"
                }).ToList();
            Assert.Single(acquisitions);
            Assert.Single(acquisitions[0].ArgumentList.Arguments);
            Assert.NotNull(acquisitions[0].Ancestors().OfType<LocalDeclarationStatementSyntax>()
                .SingleOrDefault(d => !d.UsingKeyword.IsKind(
                    Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)));
        });
    }

    [Fact]
    public void AlreadyConstructedWalletDoesNotReacquireNativeAccessDuringLookup()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var model = plugin.Model(tree);
        var method = RoslynPins.Method(tree, "RgbLibService", "GetOrCreateWalletAsync");
        var body = RoslynPins.BodyOf(method);

        var cachedReturn = body.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Single(r => r.Expression?.ToString() == "cachedWallet.Value");
        var cachedGuard = cachedReturn.Ancestors().OfType<IfStatementSyntax>().Single();
        Assert.Contains(cachedGuard.Condition.DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>(),
            member => member.ToString() == "cachedWallet.IsValueCreated");

        var processGate = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "WithProcessGate",
                ContainingType.Name: "RgbNativeSendLease"
            });
        var walletAccess = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "AcquireWalletConstructionAccess",
                ContainingType.Name: "RgbNativeSendLease"
            });
        Assert.True(cachedReturn.SpanStart < processGate.SpanStart);
        Assert.True(cachedReturn.SpanStart < walletAccess.SpanStart);
    }

    [Fact]
    public void EveryCachedNativeDisposalAcquiresWalletAccessLease()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibWalletHandle.cs");
        var model = plugin.Model(tree);
        foreach (var methodName in new[] { "Dispose", "CompleteTimedOutDispose" })
        {
            var method = RoslynPins.Method(tree, "RgbLibWalletHandle", methodName);
            var body = RoslynPins.BodyOf(method);
            var acquisition = Assert.Single(body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
                {
                    Name: "AcquireWalletAccess",
                    ContainingType.Name: "RgbNativeSendLease"
                });
            Assert.Equal(2, acquisition.ArgumentList.Arguments.Count);
            Assert.Contains(acquisition.ArgumentList.Arguments,
                a => a.NameColon?.Name.Identifier.ValueText == "allowMarked"
                     && a.Expression.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.TrueLiteralExpression));
            Assert.Single(body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
                {
                    Name: "DisposeNativeWallet",
                    ContainingType.Name: "RgbLibWalletHandle"
                });
        }
    }

    [Fact]
    public void WalletConstructionCannotBypassMarkerOwnership()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var method = RoslynPins.Method(tree, "RgbLibService", "GetOrCreateWalletAsync");
        var body = RoslynPins.BodyOf(method);
        var model = plugin.Model(tree);
        var acquire = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "AcquireWalletConstructionAccess",
                ContainingType.Name: "RgbNativeSendLease"
            });
        Assert.Single(acquire.ArgumentList.Arguments);

        var lazyValue = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(m => m.Name.Identifier.ValueText == "Value"
                && m.Expression.ToString() == "lazyWallet");
        Assert.True(acquire.SpanStart < lazyValue.SpanStart);
    }

    [Fact]
    public void DeferredNativeDisposalIsCoalescedBeforeScheduling()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var model = plugin.Model(tree);
        var method = RoslynPins.Method(tree, "RgbLibService", "DisposeAndEvict");
        var body = RoslynPins.BodyOf(method);
        var coalesce = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "TryStartDeferredDispose",
                ContainingType.Name: "RgbLibWalletHandle"
            });
        var guarded = coalesce.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Contains(guarded.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "Run",
                ContainingType.Name: "Task"
            });
    }

    [Fact]
    public void InFlightConstructionDisposalIsCoalescedBeforeScheduling()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var model = plugin.Model(tree);
        var method = RoslynPins.Method(tree, "RgbLibService", "UnloadFromCache");
        var body = RoslynPins.BodyOf(method);
        var coalesce = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "TryAdd",
                ContainingType.Name: "ConcurrentDictionary"
            });
        var guarded = coalesce.Ancestors().OfType<IfStatementSyntax>().Single();
        Assert.Contains(guarded.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "Run",
                ContainingType.Name: "Task"
            });
        Assert.Contains(guarded.Statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "TryRemove",
                ContainingType.Name: "ConcurrentDictionary"
            });
    }

    [Fact]
    public void PreLaunchQuarantineCannotBecomeAnUnreapedChildFailure()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var send = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync"));
        var catchClause = send.DescendantNodes().OfType<CatchClauseSyntax>()
            .Single(c => c.Declaration?.Identifier.ValueText == "sendException");
        var branches = catchClause.Block.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("sendException is", StringComparison.Ordinal))
            .ToList();
        var quarantine = branches.Single(i => i.Condition.ToString()
            .Contains("RgbWalletQuarantinedException", StringComparison.Ordinal));
        Assert.Contains("sendBeginMayHaveRun", quarantine.Condition.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("!sendEndStarted", quarantine.Condition.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(quarantine.Statement.DescendantNodes()
                .OfType<ThrowStatementSyntax>(),
            t => t.Expression != null && model.GetTypeInfo(t.Expression).Type?.Name
                == "NativeSendChildUnreapedException");

        var wrapper = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "SendAssetAsync"));
        Assert.DoesNotContain(wrapper.DescendantNodes().OfType<CatchClauseSyntax>(),
            c => c.Declaration?.Type.ToString() == "RgbWalletQuarantinedException");

        var sendEndWrite = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "WriteSendEnd",
                ContainingType.Name: "RgbSendRecoveryJournal"
            });
        var sendEndTry = sendEndWrite.Ancestors().OfType<TryStatementSyntax>().First();
        var typedPreLaunch = sendEndTry.Catches.Single(c =>
            c.Declaration?.Type.ToString() == "RgbWalletQuarantinedException");
        Assert.True(typedPreLaunch.SpanStart < sendEndTry.Catches
            .Single(c => c.Declaration == null).SpanStart);
        Assert.DoesNotContain(typedPreLaunch.Block.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            o => model.GetTypeInfo(o).Type?.Name == "NativeSendChildUnreapedException");
    }

    [Fact]
    public void HelperClaimsBothLeasesBeforeConstructingNativeWallet()
    {
        var serviceSource = Source();
        Assert.Contains(
            "LeaseToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(leaseWalletDir)",
            serviceSource, StringComparison.Ordinal);

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            File.ReadAllText(Path.Combine(RepoRoot(), "RgbRestoreHelper", "RgbNativeSend.cs")));
        var invoke = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Invoke");
        var body = RoslynPins.BodyOf(invoke);
        var calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        var worker = calls.Single(i => i.Expression.ToString().EndsWith("AcquireWorker",
            StringComparison.Ordinal));
        var access = calls.Single(i => i.Expression.ToString().EndsWith("AcquireWalletAccess",
            StringComparison.Ordinal));
        var wallet = body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "RgbLibWallet");

        Assert.True(worker.SpanStart < access.SpanStart);
        Assert.True(access.SpanStart < wallet.SpanStart);
        Assert.Contains("request.LeaseToken", worker.ArgumentList.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExactRecoveryJournalRenameHasADirectoryDurabilityBarrier()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Services", "RgbSendRecoveryJournal.cs"));
        var move = source.IndexOf("File.Move(temporary, path, overwrite: true)",
            StringComparison.Ordinal);
        var directoryFlush = source.IndexOf("FlushDirectory(directory)", move,
            StringComparison.Ordinal);

        Assert.True(move >= 0 && directoryFlush > move);
        Assert.Contains("Fsync(descriptor)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoverySecretsAreHardenedBeforeTheirFirstWrite()
    {
        var journal = File.ReadAllText(Path.Combine(
            RepoRoot(), "Services", "RgbSendRecoveryJournal.cs"));
        var journalMode = journal.IndexOf("File.SetUnixFileMode(temporary",
            StringComparison.Ordinal);
        var journalWrite = journal.IndexOf("stream.Write(bytes)", StringComparison.Ordinal);
        Assert.True(journalMode >= 0 && journalMode < journalWrite);

        var lease = File.ReadAllText(Path.Combine(
            RepoRoot(), "Services", "RgbNativeSendLease.cs"));
        var ensure = lease.IndexOf("static string EnsureDurableWorkerFile", StringComparison.Ordinal);
        var harden = lease.IndexOf("HardenWorkerFile(path)", ensure, StringComparison.Ordinal);
        var tokenWrite = lease.IndexOf("WriteWorkerToken(stream, workerToken)", ensure,
            StringComparison.Ordinal);
        Assert.True(harden >= 0 && harden < tokenWrite);
    }

    [Fact]
    public void DurableWorkerPublicationRollsBackOnlyTheFileTheSameCallCreated()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbNativeSendLease.cs");

        foreach (var publisher in new[] { "EnsureDurableWorkerFile", "CreateDurableWorkerFile" })
        {
            var body = RoslynPins.BodyOf(
                RoslynPins.Method(tree, "RgbNativeSendLease", publisher));
            var create = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(i => i.ArgumentList.Arguments
                    .Any(a => a.ToString() == "FileMode.CreateNew"));
            var rollback = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(i => i.Expression.ToString() == "RollBackNewWorkerFile");
            var guarded = rollback.Ancestors().OfType<TryStatementSyntax>().First();

            Assert.False(create.Ancestors().OfType<TryStatementSyntax>().Contains(guarded),
                $"{publisher}: the FileMode.CreateNew open must sit OUTSIDE the try whose catch calls "
                + "RollBackNewWorkerFile. Inside it, a CreateNew that failed precisely because another "
                + "owner already published the durable helper marker makes the rollback unlink a file "
                + "this call never created, and the next AcquireParent retry then walks through the "
                + "quarantine.");
        }
    }

    [Fact]
    public void InvoiceHintsCannotBypassRefreshAndExpiryCleanup()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "RGBInvoiceListener.cs"));
        var start = source.IndexOf("async Task CheckSingleInvoice", StringComparison.Ordinal);
        var end = source.IndexOf("internal static bool ShouldEnqueue", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("_queue.RequestRecovery()", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessTransfers(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RawInvoiceAndJournalAreBoundedBeforeNativeWorkOrMarkerPublication()
    {
        var source = Source();
        var sendStart = source.IndexOf("async Task<(string Txid, long AmountSent, string AssetId",
            StringComparison.Ordinal);
        var lengthGuard = source.IndexOf("rgbInvoice.Length > TransportEndpointValidator.MaxRgbInvoiceLength",
            sendStart, StringComparison.Ordinal);
        var decode = source.IndexOf("_rgbLib.DecodeInvoice(rgbInvoice)", sendStart,
            StringComparison.Ordinal);
        Assert.True(lengthGuard >= 0 && lengthGuard < decode);

        var reconcileStart = source.IndexOf("async Task ReconcileWalletRecoveryAsync",
            StringComparison.Ordinal);
        var read = source.IndexOf("RgbSendRecoveryJournal.Read(journalPath)", reconcileStart,
            StringComparison.Ordinal);
        var acquire = source.IndexOf("AcquireNativeSendRecoveryLease(walletDir)", reconcileStart,
            StringComparison.Ordinal);
        Assert.True(read >= 0 && read < acquire);
    }

    [Theory]
    [InlineData("CreateColorableUtxosAsync")]
    [InlineData("SendBtcAsync")]
    [InlineData("CleanupExpiredTransfersInternalAsync")]
    public void EveryMultiStepWalletMutationOwnsCrossProcessExclusion(string methodName)
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var entry = RoslynPins.Method(tree, "RGBWalletService", methodName);
        var body = RoslynPins.BodyOf(entry);

        if (methodName == "CreateColorableUtxosAsync")
        {
            // Both entry points deliberately delegate to one private implementation so the final
            // automatic-authorization check can run after acquiring the same lock and lease used by
            // the manual path. Follow the bound target instead of requiring the lease text to be
            // duplicated in this thin public wrapper.
            var helperCall = Assert.Single(body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
                {
                    DeclaredAccessibility: Accessibility.Private,
                    ContainingType.Name: "RGBWalletService"
                });
            var helper = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(helperCall).Symbol);
            var helperDeclaration = Assert.IsType<MethodDeclarationSyntax>(
                Assert.Single(helper.DeclaringSyntaxReferences).GetSyntax());

            var automatic = RoslynPins.BodyOf(RoslynPins.Method(
                tree, "RGBWalletService", "CreateColorableUtxosAutomaticallyAsync"));
            Assert.Single(automatic.DescendantNodes().OfType<InvocationExpressionSyntax>(), i =>
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(i).Symbol, helper));

            body = RoslynPins.BodyOf(helperDeclaration);
        }

        Assert.Single(body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "AcquireNativeSendParentLease",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.Single(body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ClearActiveMarker",
                ContainingType.Name: "RgbNativeSendLease"
            });
    }

    [Fact]
    public void WalletDeletionCannotRemoveDurableRecoveryDiscoverability()
    {
        var plugin = PluginCompilation.Shared;
        var serviceTree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(serviceTree);
        var deletion = RoslynPins.BodyOf(
            RoslynPins.Method(serviceTree, "RGBWalletService", "DeleteWalletAsync"));
        var remove = deletion.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol { Name: "Remove" });

        foreach (var required in new[] { "IsNeedsRecoveryAsync", "PathFor", "Exists" })
        {
            var guard = deletion.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .First(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol method
                            && method.Name == required);
            Assert.True(guard.SpanStart < remove.SpanStart);
        }
        var lease = deletion.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "AcquireNativeSendParentLease",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.True(lease.SpanStart < remove.SpanStart);
        var orphanGuard = deletion.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "FindOrphanedOutgoingBatchIndicesAsync",
                ContainingType.Name: "RGBWalletService"
            });
        Assert.True(lease.SpanStart < orphanGuard.SpanStart && orphanGuard.SpanStart < remove.SpanStart);
        var committedAssignment = deletion.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "deletionCommitted");
        var save = deletion.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol { Name: "SaveChangesAsync" });
        Assert.True(save.SpanStart < committedAssignment.SpanStart);
        var committedCleanupCatch = deletion.DescendantNodes().OfType<CatchClauseSyntax>()
            .Single(c => c.Filter?.FilterExpression.ToString() == "deletionCommitted");
        Assert.Contains("post-commit cleanup was incomplete", committedCleanupCatch.Block.ToString(),
            StringComparison.Ordinal);
        var deferredUnload = deletion.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("!_rgbLib.UnloadWallet(walletId)",
                StringComparison.Ordinal));
        Assert.Contains("deleteLease.ClearActiveMarker(walletDir)", deferredUnload.Statement.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("new RgbWalletQuarantinedException", deferredUnload.Statement.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("NativeSendChildUnreapedException", deferredUnload.Statement.ToString(),
            StringComparison.Ordinal);

        var controllerTree = plugin.Tree("Controllers/RGBController.cs");
        var controllerModel = plugin.Model(controllerTree);
        var action = RoslynPins.BodyOf(
            RoslynPins.Method(controllerTree, "RGBController", "DeleteWallet"));
        var deleteCall = action.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => controllerModel.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "DeleteWalletAsync"
            });
        var updateStores = action.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => controllerModel.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "UpdateStore"
            }).ToList();
        Assert.Equal(2, updateStores.Count);
        Assert.True(updateStores[0].SpanStart < deleteCall.SpanStart);
        Assert.True(deleteCall.SpanStart < updateStores[1].SpanStart);
    }

    [Fact]
    public void InactiveQuarantinedWalletsRemainDiscoverableWithoutSettlementWork()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Services", "RGBInvoiceListener.cs"));
        var start = source.IndexOf("async Task<bool> RefreshAllWallets", StringComparison.Ordinal);
        var end = source.IndexOf("async Task<bool> CleanupExpiredTransfers", start,
            StringComparison.Ordinal);
        var method = source[start..end];
        Assert.Contains("w.IsActive || w.NeedsRecovery", method, StringComparison.Ordinal);
        var refresh = method.IndexOf("RefreshWalletAsync", StringComparison.Ordinal);
        var inactiveStop = method.IndexOf("if (!w.IsActive) continue", StringComparison.Ordinal);
        var process = method.IndexOf("ProcessTransfers", StringComparison.Ordinal);
        Assert.True(refresh >= 0 && refresh < inactiveStop && inactiveStop < process);
    }

    [Fact]
    public void NativeSendHelperReceivesAndAppliesAHardMemoryLimitBeforeReadingInput()
    {
        var runner = File.ReadAllText(Path.Combine(
            RepoRoot(), "Services", "NativeSendProcessRunner.cs"));
        Assert.Contains("psi.ArgumentList.Add(limits.RamCapBytes.ToString())", runner,
            StringComparison.Ordinal);

        var program = File.ReadAllText(Path.Combine(
            RepoRoot(), "RgbRestoreHelper", "Program.cs"));
        var apply = program.IndexOf("ApplyResourceLimits(memoryLimitBytes, cpuLimitSeconds)",
            StringComparison.Ordinal);
        var read = program.IndexOf("stdin.ReadToEnd()", StringComparison.Ordinal);
        Assert.True(apply >= 0 && apply < read);

        var limiter = File.ReadAllText(Path.Combine(
            RepoRoot(), "RgbRestoreHelper", "NativeSendResourceLimiter.cs"));
        Assert.Contains("SetRLimit(resource, ref updated)", limiter, StringComparison.Ordinal);
        Assert.Contains("JobObjectLimitProcessMemory", limiter, StringComparison.Ordinal);
        Assert.Contains("JobObjectLimitProcessTime", limiter, StringComparison.Ordinal);
    }
}
