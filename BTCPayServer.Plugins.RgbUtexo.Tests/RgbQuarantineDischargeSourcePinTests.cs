using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Two properties that the whole quarantine write-ahead rests on and that NO behavioural test can reach:
/// SendLockCoordinatorTests supplies its own delegates, so the production members are never invoked by the
/// suite, and this test project has no EF provider, so there is no service-level route to them either.
///
/// Scope: these catch an ACCIDENTAL regression — a refactor, a merge, an autocomplete. They are not a defence
/// against a committer who intends to remove the control, since whoever can edit the method can edit the pin.
/// </summary>
public class RgbQuarantineDischargeSourcePinTests
{
    const string WalletFile = "Services/RGBWalletService.cs";
    const string WalletType = "RGBWalletService";
    const string WalletFullType = "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService";
    const string RgbLibFullType = "BTCPayServer.Plugins.RgbUtexo.Services.IRgbLibService";
    const string DurabilityFullType = "BTCPayServer.Plugins.RgbUtexo.Services.RgbStockDurability";

    // P5. The coordinator is wired to the right members. Four of RGBWalletService's own methods have
    // signatures compatible with these slots — SetNeedsRecoveryAsync and IsNeedsRecoveryAsync are
    // signature-identical, ClearNeedsRecoveryAsync and FsyncStockAsync are signature-identical to each other,
    // and both Task<bool> methods also satisfy a Func<..., Task> slot by return covariance — so a transposition
    // or a duplication compiles silently and restores the audit defect. On the clear slot the effect is
    // narrower than it looks — RefreshWalletAsync calls ClearNeedsRecoveryAsync directly, not through this
    // delegate, so a wrong clear slot leaves every marking operation's own quarantine standing until the next
    // refresh rather than stopping discharge outright. Written as a whitelist over parameters matched BY NAME,
    // not by index: keying on index
    // would be defeated by reordering the parameters in the coordinator's own declaration.
    [Fact]
    public void P5_CoordinatorIsWiredToTheIntendedMembers()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(WalletFile);

        // Matched on the BOUND constructor, not on the type syntax: `new Services.SendLockCoordinator(...)` and
        // target-typed `new(...)` are behaviour-preserving refactors that a syntax-only match would redden.
        var constructions = tree.GetRoot().DescendantNodes()
            .OfType<BaseObjectCreationExpressionSyntax>()
            .Where(o => plugin.Model(tree).GetSymbolInfo(o).Symbol is IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType.Name: "SendLockCoordinator"
            })
            .ToList();
        Assert.True(constructions.Count == 1,
            $"expected exactly one SendLockCoordinator construction in {WalletFile}, found {constructions.Count}");

        var creation = constructions[0];
        var ctor = (IMethodSymbol)RoslynPins.BoundSymbol(plugin, tree, creation);
        var args = creation.ArgumentList!.Arguments;
        Assert.True(args.Count == ctor.Parameters.Length,
            $"the construction must pass all {ctor.Parameters.Length} arguments, found {args.Count}: a defaulted "
            + "argument silently substitutes a delegate the coordinator then calls on every write-ahead.");

        var required = new Dictionary<string, (string Type, string Name)>
        {
            ["mark"] = (WalletFullType, "SetNeedsRecoveryAsync"),
            ["clear"] = (WalletFullType, "ClearNeedsRecoveryAsync"),
            ["evict"] = (RgbLibFullType, "UnloadWallet"),
            ["fsync"] = (WalletFullType, "FsyncStockAsync"),
        };

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].NameColon?.Name.Identifier.ValueText ?? ctor.Parameters[i].Name;
            var expression = args[i].Expression;

            if (name == "locks")
            {
                Assert.True(RoslynPins.BoundSymbol(plugin, tree, expression) is IFieldSymbol { Name: "_sendLocks" },
                    $"the 'locks' argument must be the _sendLocks field, found '{expression}': a different "
                    + "dictionary means the coordinator serialises against locks nothing else takes.");
                continue;
            }

            Assert.True(required.TryGetValue(name, out var target),
                $"unexpected constructor parameter '{name}' — add it to this pin's whitelist deliberately, "
                + "because an unpinned delegate slot is exactly the gap this test exists to close.");

            // A forwarding lambda is accepted only when its body IS the invocation and every argument it
            // forwards is the lambda's own parameter. The evict argument is a lambda today, so lambdas cannot
            // simply be skipped: skipping them would let `fsync: (id, ct) => ClearNeedsRecoveryAsync(id, ct)`
            // through, which is the mis-wiring with no other coverage at all.
            // The body may be awaited: `async (id, ct) => await FsyncStockAsync(id, ct)` is a forwarding
            // lambda the design explicitly permits, and an earlier form of this pin reddened against it.
            static InvocationExpressionSyntax? Forwarded(SyntaxNode? body) => body switch
            {
                InvocationExpressionSyntax call => call,
                AwaitExpressionSyntax { Expression: InvocationExpressionSyntax call } => call,
                _ => null
            };
            var invoked = expression switch
            {
                SimpleLambdaExpressionSyntax lambda => Forwarded(lambda.Body),
                ParenthesizedLambdaExpressionSyntax lambda => Forwarded(lambda.Body),
                _ => null
            };

            SyntaxNode bindTarget;
            if (invoked != null)
            {
                var lambdaParameters = expression switch
                {
                    SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter.Identifier.ValueText },
                    ParenthesizedLambdaExpressionSyntax paren =>
                        paren.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToArray(),
                    _ => []
                };
                foreach (var forwarded in invoked.ArgumentList.Arguments)
                    Assert.True(forwarded.Expression is IdentifierNameSyntax id
                                && lambdaParameters.Contains(id.Identifier.ValueText),
                        $"the '{name}' lambda must forward only its own parameters, found "
                        + $"'{forwarded.Expression}': a substituted argument — a literal, or "
                        + "CancellationToken.None — compiles and silently changes what the coordinator calls.");
                bindTarget = invoked;
            }
            else
            {
                Assert.True(expression is IdentifierNameSyntax or MemberAccessExpressionSyntax,
                    $"the '{name}' argument must be a method group or a single-invocation forwarding lambda, "
                    + $"found '{expression}'. Any other shape — a local, a property, a conditional, a "
                    + "multi-statement lambda — is refused rather than inspected, because an indirection here "
                    + "is precisely what should be reviewed instead of waved through.");
                bindTarget = expression;
            }

            RoslynPins.AssertBindsToMemberOf(plugin, tree, bindTarget, SymbolKind.Method,
                target.Type, target.Name, $"{WalletFile} SendLockCoordinator construction, '{name}' argument");
        }
    }

    // P1 (reduced). The discharge's POSITION, which the whole design rests on and which nothing else pins.
    // Inside the coordinator's delegate: the coordinator releases the send lock before returning, so a clear
    // placed after the call commits unlocked and — because SetNeedsRecoveryAsync early-returns on an already-set
    // flag — can discharge a quarantine a different holder set microseconds earlier. After the RefreshAsync:
    // that call is what reconciles the Stock, so a discharge above it certifies nothing. Both halves were got
    // wrong once each while this change was being designed, and the natural "tidying" edit in either direction
    // silently restores a false ACCEPT that no behavioural test in this project can observe.
    [Fact]
    public void P1_DischargeSitsInsideTheDelegateAfterTheRefresh()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(WalletFile);
        var method = RoslynPins.Method(tree, WalletType, "RefreshWalletAsync");
        var body = RoslynPins.BodyOf(method);

        var coordinatorCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => plugin.Model(tree).GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "TryWithSendLockAsync",
                ContainingType.Name: "SendLockCoordinator"
            })
            .ToList();
        Assert.True(coordinatorCalls.Count == 1,
            $"RefreshWalletAsync must make exactly one TryWithSendLockAsync call, found {coordinatorCalls.Count}");

        var delegateArg = coordinatorCalls[0].ArgumentList.Arguments
            .Select(a => a.Expression)
            .FirstOrDefault(e => e is AnonymousFunctionExpressionSyntax);
        Assert.True(delegateArg != null,
            "the coordinator call must be given a lambda: passing a method group or a pre-built Task moves the "
            + "work outside the region the write-ahead covers.");

        var reconciliationCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => plugin.Model(tree).GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ReconcileWalletRecoveryAsync",
                ContainingType.Name: WalletType
            })
            .ToList();
        Assert.True(reconciliationCalls.Count == 1,
            $"RefreshWalletAsync must reconcile exactly once, found {reconciliationCalls.Count}");
        Assert.True(reconciliationCalls[0].Ancestors().Contains(delegateArg!),
            "reconciliation must sit INSIDE the coordinator's delegate — outside it the send lock is already "
            + "released, so it can clear a quarantine another holder just set.");

        var reconcileMethod = RoslynPins.Method(tree, WalletType, "ReconcileWalletRecoveryAsync");
        var reconcileBody = RoslynPins.BodyOf(reconcileMethod);
        var clears = reconcileBody.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => plugin.Model(tree).GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ClearNeedsRecoveryAsync",
                ContainingType.Name: WalletType
            })
            .ToList();
        Assert.True(clears.Count == 1, $"reconciliation must discharge exactly once, found {clears.Count}");
        var refreshes = reconcileBody.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => plugin.Model(tree).GetSymbolInfo(i).Symbol is IMethodSymbol { Name: "RefreshAsync" })
            .ToList();
        Assert.NotEmpty(refreshes);
        Assert.True(refreshes.All(r => r.SpanStart < clears[0].SpanStart),
            "the discharge must follow RefreshAsync: that call is what reconciles the Stock, so discharging "
            + "before it certifies a wallet nothing has reconciled.");

        // The database flag is what makes the wallet DISCOVERABLE to the listener's
        // (IsActive || NeedsRecovery) page; the marker and journal are what make it UNSENDABLE and
        // undeletable. Clearing the flag first leaves a crash window in which an inactive wallet keeps
        // both artifacts and is enumerated by nothing that would remove them. Artifacts first, flag last.
        // Scoped to the block that completes the reconciliation: this method also clears the marker in
        // its `finally`, which is a different block and a different purpose.
        var completion = reconcileBody.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "completed"
                         && a.Right.IsKind(SyntaxKind.TrueLiteralExpression));
        var successBlock = completion.Ancestors().OfType<BlockSyntax>().First();

        var markerClear = successBlock.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => plugin.Model(tree).GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ClearActiveMarker",
                ContainingType.Name: "RgbNativeSendLease"
            });
        var journalDelete = successBlock.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => plugin.Model(tree).GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "Delete",
                ContainingType.Name: "RgbSendRecoveryJournal"
            });
        Assert.Contains(clears[0], successBlock.DescendantNodes().OfType<InvocationExpressionSyntax>());
        Assert.True(markerClear.SpanStart < clears[0].SpanStart,
            "the worker marker must be cleared BEFORE NeedsRecovery is committed false — otherwise a crash "
            + "between them leaves a marker that refuses AcquireParent on a wallet nothing re-arms.");
        Assert.True(journalDelete.SpanStart < clears[0].SpanStart,
            "the recovery journal must be deleted BEFORE NeedsRecovery is committed false — otherwise a crash "
            + "between them leaves a journal that refuses every send on a wallet nothing re-arms.");
    }

    [Fact]
    public void TheSuccessfulSendPathAlsoDropsTheJournalBeforeClearingTheFlag()
    {
        // Same ordering, on the path that runs after EVERY successful send rather than only after a
        // reconciliation, which makes it the high-frequency instance of the same crash window.
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(WalletFile);
        var model = plugin.Model(tree);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, WalletType, "SendAssetInternalAsync"));

        var clear = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "ClearNeedsRecoveryAsync",
                ContainingType.Name: WalletType
            });
        var journalDelete = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "Delete",
                ContainingType.Name: "RgbSendRecoveryJournal"
            });

        Assert.True(journalDelete.SpanStart < clear.SpanStart,
            "the journal delete must precede the NeedsRecovery clear on the send path too; a crash between "
            + "them in the other order leaves a journal that refuses every subsequent send.");

        // The marker is not moved ahead of the flag — OneOperationLeaseEnclosesBothNativeHelperPhases
        // requires this method to release it exactly once from a finally, which is what guarantees release
        // on the failure paths. So the flag is moved BEHIND the marker instead: the discharge sits past the
        // whole try/finally, which orders it after the single release without touching it. Pinned against
        // the try statement that owns the release rather than against a line number, because moving the
        // discharge back inside that try is the regression, wherever in it it lands.
        var markerClear = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol { Name: "ClearActiveMarker" });
        var releasingTry = markerClear.Ancestors().OfType<TryStatementSyntax>()
            .Single(t => t.Finally != null && t.Finally.Span.Contains(markerClear.Span));

        Assert.True(clear.SpanStart > releasingTry.Span.End,
            "the NeedsRecovery discharge must run after the try/finally that releases the worker marker, so "
            + "no window exists in which the flag reads false while an artifact no scan revisits is still "
            + "on disk.");
    }

    // P7. rgb-lib's GetBtcBalance takes skipSync, the INVERSE of this plugin's `sync`. Passing `sync` straight
    // through shipped in production and reversed every caller silently: the three sites asking for a sync got
    // none, while the page loads taking the `sync: false` default were the only ones syncing. Nothing could
    // observe it — the balance is still returned, just from unsynced state — so the only guard available is on
    // the shape of the call. Pinned as "the argument is a negation of the method's own sync parameter", because
    // the plausible regression is a future reader "simplifying" the `!` away.
    [Fact]
    public void P7_BtcBalanceNegatesSyncIntoSkipSync()
    {
        var plugin = PluginCompilation.Shared;
        const string file = "Services/RgbLibService.cs";
        var tree = plugin.Tree(file);
        var method = RoslynPins.Method(tree, "RgbLibService", "GetBtcBalanceAsync");
        var body = RoslynPins.BodyOf(method);

        // The balance no longer goes through RgbLib's typed GetBtcBalance wrapper — that wrapper marshals
        // a CResultString the package never frees — so the negation this pin exists for now sits in the
        // reflected call's argument array. Same property, new location.
        var arrays = body.DescendantNodes().OfType<ArrayCreationExpressionSyntax>()
            .Where(a => a.Initializer != null)
            .ToList();
        Assert.True(arrays.Count == 1,
            $"expected exactly one native argument array in GetBtcBalanceAsync in {file}, found {arrays.Count}");

        var args = arrays[0].Initializer!.Expressions;
        Assert.True(args.Count == 3,
            $"rgblib_get_btc_balance takes wallet, online and skip_sync — found {args.Count} arguments");

        Assert.True(args[2] is PrefixUnaryExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.LogicalNotExpression,
                        Operand: IdentifierNameSyntax { Identifier.ValueText: "sync" }
                    },
            $"the skip_sync argument must be `!sync` — rgb-lib's flag is the inverse of this method's, so passing "
            + $"`sync` unchanged reverses every caller silently; found '{args[2]}'.");
    }

    // P6 clause 1. SetNeedsRecoveryAsync's return polarity. WriteAheadAsync's `if (marked)` requires "true means
    // THIS call set the flag"; inverting the two literals compiles, is invisible to every test, and produces the
    // exact false-ACCEPT the change exists to close — a pre-quarantined wallet reports true, so the coordinator
    // takes the clear branch and discharges a quarantine nothing reconciled. The name is genuinely ambiguous
    // about direction ("is quarantined" vs "I set it"), which is what makes the flip an ordinary edit.
    [Fact]
    public void P6_MarkReportsWhetherItSetTheFlag()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(WalletFile);
        var model = plugin.Model(tree);
        var method = RoslynPins.Method(tree, WalletType, "SetNeedsRecoveryAsync");
        var body = RoslynPins.BodyOf(method);

        var guards = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition is MemberAccessExpressionSyntax access
                        && model.GetSymbolInfo(access).Symbol is IPropertySymbol { Name: "NeedsRecovery" })
            .ToList();
        Assert.True(guards.Count == 1,
            "SetNeedsRecoveryAsync must contain exactly one guard whose condition IS the un-negated "
            + $"NeedsRecovery member access, found {guards.Count}. Stated positively on purpose: negating the "
            + "condition — `if (!w.NeedsRecovery) return false;` — satisfies a clause that only pins the two "
            + "return literals, while never marking a clean wallet and discharging a quarantined one.");

        var guarded = guards[0].Statement is BlockSyntax block
            ? block.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()
            : guards[0].Statement as ReturnStatementSyntax;
        Assert.True(guarded?.Expression?.IsKind(SyntaxKind.FalseLiteralExpression) == true,
            $"the guarded branch must `return false` — the flag was already set, so this call did not set it; "
            + $"found '{guards[0].Statement.ToString().Trim()}'.");

        var returns = body.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
        Assert.True(returns.Count == 2,
            $"SetNeedsRecoveryAsync must have exactly two returns, found {returns.Count}");
        Assert.True(returns[^1].Expression?.IsKind(SyntaxKind.TrueLiteralExpression) == true,
            "the final return must be `true` — reached only after SaveChangesAsync committed the flag; found "
            + $"'{returns[^1].ToString().Trim()}'.");
    }

    // P6 clause 2. FsyncStockAsync must actually fsync, unconditionally, before returning. Emptying the body,
    // swallowing the failure, guarding the call, wrapping it in a loop, returning early above it, or deferring
    // it to a Task all compile and silently void "every successful write-ahead leaves the Stock durable" — which
    // is what makes IssueAssetAsync able to commit an RGBAssets row whose Stock issuance never reached disk.
    [Fact]
    public void P6_FsyncStockActuallyFsyncsUnconditionally()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(WalletFile);
        var method = RoslynPins.Method(tree, WalletType, "FsyncStockAsync");
        var body = RoslynPins.BodyOf(method);

        var calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "FsyncStockDats" })
            .ToList();
        Assert.True(calls.Count == 1,
            $"FsyncStockAsync must invoke FsyncStockDats exactly once, found {calls.Count}");
        var call = calls[0];
        RoslynPins.AssertBindsToMemberOf(plugin, tree, call, SymbolKind.Method,
            DurabilityFullType, "FsyncStockDats", $"{WalletFile} FsyncStockAsync");

        var ancestors = call.Ancestors().TakeWhile(a => a != body.Parent).ToList();

        Assert.True(!ancestors.Any(a => a is TryStatementSyntax { Catches.Count: > 0 }),
            "the FsyncStockDats call must not sit inside a try with a catch: swallowing the failure lets the "
            + "coordinator report success over a Stock that never reached disk.");
        Assert.True(!ancestors.Any(a => a is IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax
                                             or ForEachStatementSyntax or DoStatementSyntax or SwitchStatementSyntax),
            "the FsyncStockDats call must not sit inside a conditional, a loop or a switch: wrapping it — "
            + "`if (Directory.Exists(stockDir)) FsyncStockDats(stockDir);` — makes the durability barrier "
            + "optional while every other clause here stays green.");

        Assert.True(!body.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Any(r => r.Expression == null && r.SpanStart < call.SpanStart),
            "no bare `return;` may precede the FsyncStockDats call, anywhere in the body including inside a "
            + "try/catch: an early exit above it skips the fsync with the suite green.");

        Assert.True(call.Parent is ExpressionStatementSyntax,
            $"the FsyncStockDats call must be consumed as a statement so it completes before the method "
            + $"returns, found parent {call.Parent?.Kind()}.");

        // Checking the parent alone is not enough, and this is the second time that exact gap has been found:
        // `_ = Task.Run(() => { FsyncStockDats(...); }, ct)` puts the call inside a BLOCK, so its parent is
        // still an ExpressionStatement and no ancestor is a conditional — every other clause here stays green
        // while WriteAheadAsync can return before the Stock is durable. Any anonymous function or local
        // function between the call and the method body means something else decides when it runs.
        Assert.True(!ancestors.Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax),
            "the FsyncStockDats call must not sit inside a lambda, anonymous method or local function: whatever "
            + "receives that delegate decides when — or whether — the fsync happens, so the durability barrier "
            + "stops being synchronous with the write-ahead.");
    }
}
