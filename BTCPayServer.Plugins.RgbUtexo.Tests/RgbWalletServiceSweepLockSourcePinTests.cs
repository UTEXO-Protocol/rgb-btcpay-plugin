using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Pins that the periodic settlement sweep acquires the per-wallet send lock NON-blockingly and does its
/// work inside the delegate the coordinator awaits. The behavioural pair next door catches a blocking
/// acquisition and a cleanup that runs anyway; it cannot catch a cleanup moved OUTSIDE the coordinator's
/// delegate, or one whose task the delegate never consumes — with the lock held such a body does nothing
/// observable, and with the lock free the write-ahead's _mark throws before control reaches it. Those are
/// unsynchronised writes that no test in this harness can see, which is what these clauses are for.
///
/// Scope: this catches an ACCIDENTAL regression — a refactor, a merge, a well-meaning simplification. It
/// is not a defence against a committer who intends to remove the control, since whoever can edit the
/// method can edit the pin.
/// </summary>
public class RgbWalletServiceSweepLockSourcePinTests
{
    const string WalletFile = "Services/RGBWalletService.cs";
    const string WalletType = "RGBWalletService";
    // Fully qualified for symbol comparison: a same-simple-named type in another namespace satisfies a
    // simple-name compare while the pinned member never runs.
    const string WalletFullType = "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService";
    const string CoordinatorFullType = "BTCPayServer.Plugins.RgbUtexo.Services.SendLockCoordinator";
    const string Cleanup = "CleanupExpiredTransfersAsync";
    const string Internal = "CleanupExpiredTransfersInternalAsync";

    // Limb 1 (the invoked member is declared on the coordinator) fires for every shape this change
    // produces, including a static or fully-qualified call. Limb 2 exists only for an extension method
    // taking `this SendLockCoordinator`, whose containing type is the static class and which limb 1
    // therefore misses. Keyed on IsExtensionMethod, NOT on the receiver's type: SendLockCoordinator is
    // sealed and overrides nothing, so _sendCoordinator.ToString() binds to an IMethodSymbol whose
    // ContainingType is System.Object — a receiver-type limb would fire on it and redden a CORRECT body.
    // Extension methods cannot be inherited object members, so this form excludes that by construction.
    static bool TargetsCoordinator(PluginCompilation plugin, SyntaxTree tree, InvocationExpressionSyntax i)
    {
        if (plugin.Model(tree).GetSymbolInfo(i).Symbol is not IMethodSymbol m) return false;
        return m.ContainingType?.ToDisplayString() == CoordinatorFullType
            || (m.IsExtensionMethod && m.ReceiverType?.ToDisplayString() == CoordinatorFullType);
    }

    // One fact, not eight: all eight clauses assert on the same method in the same compilation, and
    // clause 1's attribution depends on its count running before its bind. Split into separate facts, a
    // future edit could reorder or delete one half silently.
    [Fact]
    public void SweepCleanup_AcquiresSendLockNonBlockingly_AndRunsTheWorkInsideIt()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(WalletFile);
        var method = RoslynPins.Method(tree, WalletType, Cleanup);

        var coordinatorCalls = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => TargetsCoordinator(plugin, tree, i))
            .ToList();
        Assert.True(coordinatorCalls.Count == 1,
            $"{Cleanup} must make exactly one SendLockCoordinator call, found {coordinatorCalls.Count}: "
            + "the settlement sweep may not block on a busy wallet's send lock (audit H2c-lite). "
            + string.Join(" | ", coordinatorCalls.Select(c => c.ToString())));

        // Pins WHICH member the one coordinator-targeting call names. It is not what catches a shadow: a
        // local function named TryWithSendLockAsync binds with ContainingType == RGBWalletService, and a
        // same-named type from another namespace gives a different display string, so TargetsCoordinator
        // excludes both and the count above reddens at 0 first.
        RoslynPins.AssertBindsToMemberOf(plugin, tree, coordinatorCalls[0], SymbolKind.Method,
            CoordinatorFullType, "TryWithSendLockAsync", $"{WalletFile} {Cleanup}");

        var internalCalls = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => plugin.Model(tree).GetSymbolInfo(i).Symbol is IMethodSymbol m
                        && m.Name == Internal && m.ContainingType?.ToDisplayString() == WalletFullType)
            .ToList();
        Assert.True(internalCalls.Count == 1,
            $"{Cleanup} must call {Internal} exactly once, found {internalCalls.Count}. If you extracted the "
            + "body to a separate private method and passed its method group, this pin no longer covers the "
            + "call and MUST be moved with it (documented ablation row R-f).");

        // A COUNT of enclosing executable bodies, not a property of the nearest one. LocalFunctionStatementSyntax
        // is not a BaseMethodDeclarationSyntax, so the walk passes through a local function and terminates at the
        // method declaration.
        var bodies = internalCalls[0].Ancestors()
            .TakeWhile(a => a is not BaseMethodDeclarationSyntax)
            .Count(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
        Assert.True(bodies == 1,
            $"the {Internal} call is nested {bodies} executable bodies deep inside {Cleanup}; exactly one is "
            + "required. Zero means it sits among the method's own statements and runs OUTSIDE the coordinator's "
            + "delegate. Two or more means the delegate the coordinator awaits is not the one doing the work — e.g. "
            + "Task.Factory.StartNew(() => …), whose outer task completes as soon as the inner one STARTS, so the "
            + "lock is released and the write-ahead cleared while the cleanup is still running.");

        // Whitelist, not blacklist: the property is "the task is consumed", so an unrecognised position must
        // redden. A blacklist of discard shapes would let an unanticipated discard form pass silently.
        var consumed = internalCalls[0].Parent is AnonymousFunctionExpressionSyntax   // () => Internal(...)
                                                or ArrowExpressionClauseSyntax        // local function => Internal(...)
                                                or ReturnStatementSyntax              // return Internal(...);
                                                or AwaitExpressionSyntax;             // await Internal(...)
        Assert.True(consumed,
            $"the {Internal} call's task is not returned or awaited by the delegate passed to the coordinator, so the "
            + "lock is released before the cleanup completes. If you added a legitimate consuming form, add it here — "
            + "this whitelist is append-only, and an unlisted position must redden rather than pass.");

        // Clauses 1-3 are all anchored on the internal call's OWN position, which makes them structurally blind
        // to a defect introduced BETWEEN the delegate the coordinator awaits and the work. Compose the blessed
        // local-function extraction (`Task Local() => Internal(...);`) with any accidental misuse at Local's
        // CALL site and every earlier clause still passes: the internal call remains one body deep with an
        // allowed parent, because the mistake is applied to `Local()` and not to the internal call. Two such
        // compositions were found in review and both were measured silent with all tests green —
        // `() => Task.Factory.StartNew(Local)` (row M8) and `() => { _ = Local(); return Task.CompletedTask; }`
        // (row M12). Both are R-d1 composed with a bug this suite already classifies as accidental, so a
        // per-API ban would just invite the third. This clause instead FOLLOWS THE DELEGATE: whatever the
        // coordinator is handed, the internal call must live inside it.
        var model = plugin.Model(tree);
        var coordinator = (IMethodSymbol)model.GetSymbolInfo(coordinatorCalls[0]).Symbol!;
        var arguments = coordinatorCalls[0].ArgumentList.Arguments;
        ExpressionSyntax? op = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var parameterName = arguments[i].NameColon?.Name.Identifier.ValueText
                ?? (i < coordinator.Parameters.Length ? coordinator.Parameters[i].Name : null);
            if (parameterName == "op") { op = arguments[i].Expression; break; }
        }
        Assert.True(op is not null,
            $"{Cleanup}: could not locate the coordinator's 'op' argument. This pin matches that parameter BY "
            + "NAME, so renaming 'op' on SendLockCoordinator.TryWithSendLockAsync breaks it — the fix is in "
            + "SendLockCoordinator.cs, not here: either keep the name or update this lookup to match.");

        // A lambda is its own body; a by-name reference (a local function or a Func<Task> local, which the
        // documented refactors R-d1/R-d2/R-e all produce) resolves to its declaration.
        SyntaxNode? delegateBody = op as AnonymousFunctionExpressionSyntax;
        if (delegateBody is null && op is IdentifierNameSyntax reference)
            delegateBody = model.GetSymbolInfo(reference).Symbol?
                .DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();
        Assert.True(delegateBody is not null,
            $"{Cleanup}: the coordinator's 'op' argument is '{op}', which is neither a lambda nor a resolvable "
            + "local function / local. Pass the work as a lambda or by name so this pin can follow it.");

        Assert.True(internalCalls[0].Ancestors().Contains(delegateBody),
            $"the {Internal} call does not live inside the delegate handed to the coordinator ('{op}'), so "
            + "whatever the coordinator awaits is not what does the work: the lock is released and the "
            + "write-ahead cleared while the cleanup is still running, or the cleanup never runs under the lock "
            + "at all. This fires when the delegate wraps or discards a call to the extracted body instead of "
            + "returning it — e.g. Task.Factory.StartNew(Local), _ = Local(), Task.FromResult(Local()) "
            + "(audit H2c-lite rows M8, M12). Return or await the work from the delegate the coordinator gets.");

        // Clause 4 proves the coordinator awaits the work; it does NOT prove the work's FAILURE reaches the
        // coordinator, and the write-ahead depends on exactly that: WriteAheadAsync only evicts and skips
        // _clear when op() throws (see CleanupExpiredTransfersInternalAsync's own comment, which requires the
        // refresh failure to propagate so the wallet stays quarantined). A catch between the delegate and the
        // work therefore clears the write-ahead over a FAILED cleanup while every other clause passes —
        // measured silent with both behavioural tests green (audit H2c-lite row M13). This is the most
        // plausible accidental shape of the family, because the sweep is best-effort inside RefreshAllWallets'
        // per-wallet loop, so "don't let the sweep break the loop" hardening lands naturally INSIDE the lambda.
        // try/finally with no catch is fine and stays green: the exception still propagates.
        var swallowing = internalCalls[0].Ancestors()
            .TakeWhile(a => a != delegateBody)
            .OfType<TryStatementSyntax>()
            .Where(t => t.Catches.Count > 0)
            .ToList();
        Assert.True(swallowing.Count == 0,
            $"the {Internal} call sits inside a try/catch within the delegate handed to the coordinator, so a "
            + "failed cleanup is hidden from SendLockCoordinator.WriteAheadAsync: it would clear NeedsRecovery "
            + "over a cleanup that did not finish, instead of leaving the wallet quarantined and evicting the "
            + "rgb-lib handle. Let the exception propagate — the caller (RGBInvoiceListener) already swallows it "
            + "so one wallet's failure cannot break the sweep loop. try/finally without a catch is fine. A catch "
            + "that unconditionally rethrows is also safe but still reddens here; hoist it outside the "
            + "coordinator call if you need one.");

        // The coordinator locks per WALLET ID, so the work must be the work for THAT wallet. Nothing above
        // checks argument identity: the internal method takes three same-typed strings, so transposing them
        // type-checks, silently makes the sweep a permanent no-op (dbPath resolves under a directory that does
        // not exist, so the File.Exists guard returns 0), and still lets _clear clear NeedsRecovery — with both
        // behavioural tests green (audit H2c-lite rows M14, M15). The mirror image, passing the wrong id to the
        // COORDINATOR, is already caught behaviourally, which is what makes this gap one-sided.
        // Each argument must be this method's own parameter of the same name; named arguments are fine.
        // Applied to BOTH calls. On the internal call it stops a transposition; on the coordinator call it stops
        // a wallet id that does not match the work (so the lock would guard the wrong wallet) and a substituted
        // cancellation token — CancellationToken.None there also reaches _mark and _clear, re-enabling the
        // multi-minute EF retry that this suite's own factory exists to avoid (rows M14, M15, M17, M18).
        void AssertForwardsOwnParameters(InvocationExpressionSyntax call, string? skip)
        {
            var target = (IMethodSymbol)model.GetSymbolInfo(call).Symbol!;
            var passedArgs = call.ArgumentList.Arguments;
            Assert.True(passedArgs.Count == target.Parameters.Length,
                $"{Cleanup} must pass all {target.Parameters.Length} arguments to {target.Name}, found "
                + $"{passedArgs.Count}: a defaulted argument silently substitutes CancellationToken.None or "
                + "sweeps the wrong wallet.");
            for (var i = 0; i < passedArgs.Count; i++)
            {
                var parameterName = passedArgs[i].NameColon?.Name.Identifier.ValueText
                    ?? target.Parameters[i].Name;
                if (parameterName == skip) continue;
                var passed = model.GetSymbolInfo(passedArgs[i].Expression).Symbol as IParameterSymbol;
                Assert.True(passed is not null && passed.ContainingSymbol.Name == Cleanup
                            && passed.Name == parameterName,
                    $"{target.Name}'s '{parameterName}' argument must be {Cleanup}'s own '{parameterName}' "
                    + $"parameter, but '{passedArgs[i].Expression}' was passed. The coordinator locks per wallet "
                    + "id while the cleanup opens a path built from three same-typed strings, so a transposed or "
                    + "substituted argument compiles, guards or sweeps the wrong thing, and still clears the "
                    + "write-ahead (audit H2c-lite rows M14, M15, M17, M18).");
            }
        }
        AssertForwardsOwnParameters(internalCalls[0], skip: null);
        AssertForwardsOwnParameters(coordinatorCalls[0], skip: "op");

        // The coordinator ALREADY holds this wallet's semaphore for the duration of the delegate, so acquiring it
        // again inside the delegate self-deadlocks and hangs the sweep forever — the exact liveness failure this
        // change exists to remove. This is not a hypothetical: direct `_sendLocks.GetOrAdd(...)` +
        // `await sendLock.WaitAsync(ct)` is the DOMINANT idiom in this file (six sites), all of them for
        // operations that do not go through the coordinator, so copying it in here is a natural mistake.
        // Measured silent: all six earlier clauses and both behavioural tests pass (audit H2c-lite row M16).
        var directLock = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<SimpleNameSyntax>()
            .Where(n => model.GetSymbolInfo(n).Symbol is IFieldSymbol { Name: "_sendLocks" }
                                                      or IMethodSymbol { Name: "SendLockFor" })
            .ToList();
        Assert.True(directLock.Count == 0,
            $"{Cleanup} must not touch the send-lock dictionary directly, found {directLock.Count} reference(s): "
            + "the coordinator holds this wallet's semaphore for the whole delegate, so acquiring it again "
            + "self-deadlocks and the sweep never completes. Take the lock only through the coordinator. The six "
            + "direct acquisition sites elsewhere in this file are operations that do NOT go through it.");

        // Every earlier clause proves the cleanup call EXISTS in the right place; none proves it is REACHED. An
        // inverted guard inside the delegate — `if (!string.IsNullOrEmpty(walletId)) return;` before the await —
        // satisfies all seven and makes the sweep a permanent no-op, silently, with both behavioural tests green
        // (audit H2c-lite row M19). Polarity inversion is a classic accidental edit, so this is closed rather
        // than disclosed. The principle that makes the constraint reasonable rather than arbitrary: the delegate
        // exists ONLY to run the cleanup, so any decision about WHETHER to sweep belongs outside the coordinator
        // call, where it also avoids taking a lock in order to do nothing. Measured: none of the ten correct
        // bodies in the campaign contains a conditional or a bare return inside the delegate.
        var gates = internalCalls[0].Ancestors()
            .TakeWhile(a => a != delegateBody)
            .Where(a => a is IfStatementSyntax or SwitchStatementSyntax or SwitchExpressionSyntax
                          or ConditionalExpressionSyntax or WhileStatementSyntax or ForStatementSyntax
                          or ForEachStatementSyntax or DoStatementSyntax)
            .ToList();
        Assert.True(gates.Count == 0,
            $"the {Internal} call is guarded by {gates.Count} conditional/loop construct(s) inside the delegate, so "
            + "the sweep runs only on some paths. Decide whether to sweep BEFORE acquiring the lock, not inside "
            + "the delegate (audit H2c-lite row M19).");

        var earlyExits = delegateBody.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(r => r.Expression is null)
            .ToList();
        Assert.True(earlyExits.Count == 0,
            $"the delegate handed to the coordinator contains {earlyExits.Count} bare 'return;' statement(s), so it "
            + "can exit before running the cleanup — an inverted guard condition then makes the sweep a permanent "
            + "no-op while every other clause still passes (audit H2c-lite row M19). Put the condition outside the "
            + "coordinator call so a skip does not acquire the lock at all.");
    }
}
