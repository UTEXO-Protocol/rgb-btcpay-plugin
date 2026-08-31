using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPaymentRegistrationTests
{
    const string ListenerFile = "Services/RGBInvoiceListener.cs";

    // ---- the decisions, tested directly ----------------------------------------------------------

    [Theory] // G2-T1a, and the WaitingConfirmations row is G2-T7
    [InlineData(RGBInvoiceStatus.Settled, true, false)]
    [InlineData(RGBInvoiceStatus.Settled, false, true)]
    [InlineData(RGBInvoiceStatus.Underpaid, true, false)]
    [InlineData(RGBInvoiceStatus.Underpaid, false, true)]
    [InlineData(RGBInvoiceStatus.WaitingConfirmations, true, false)]
    [InlineData(RGBInvoiceStatus.WaitingConfirmations, false, true)]
    public void ShouldCommitAdvance_BlocksEveryAdvanceAfterAFailedRegistration(
        RGBInvoiceStatus status, bool registrationFailed, bool expected)
    {
        // The earlier form of this test asserted that WaitingConfirmations may commit after a failure
        // because it "self-heals". It does not: once the row leaves Pending, the waiting branch of
        // EvaluateInvoiceState stops matching it, so the failed registration is never re-attempted
        // while the transfer stays at status 2. Only the advance being held returns the row to that
        // branch. See WaitingConfirmations_HeldAfterAFailure_IsRetriedOnTheNextSweep below.
        Assert.Equal(expected, RGBInvoiceListener.ShouldCommitAdvance(status, registrationFailed));
    }

    [Fact]
    public void ShouldCommitAdvance_WithNoAdvanceToMake_IsNeverBlocked()
    {
        // A null status carries no payment work, so registrationFailed cannot be set for it; blocking
        // here would be a hold with nothing to retry.
        Assert.True(RGBInvoiceListener.ShouldCommitAdvance(null, true));
        Assert.True(RGBInvoiceListener.ShouldCommitAdvance(null, false));
    }

    [Fact]
    public void WaitingConfirmations_HeldAfterAFailure_IsRetriedOnTheNextSweep()
    {
        var invoice = new RGBInvoice
        {
            Id = "inv-1", WalletId = "w", RecipientId = "r", AssetId = "a",
            Amount = 100, Status = RGBInvoiceStatus.Pending
        };
        var inFlight = new[] { new RgbTransfer { Idx = 1, Status = 2, Amount = 100, Txid = "tx" } };

        var first = RGBInvoiceListener.EvaluateInvoiceState(invoice, inFlight);
        Assert.Equal(RGBInvoiceStatus.WaitingConfirmations, first.NewStatus);
        Assert.Equal(PaymentStatus.Processing, first.PaymentStatus);
        Assert.Single(first.PaymentsToRecord);

        // The gate refuses the advance, so the row keeps the status it had.
        Assert.False(RGBInvoiceListener.ShouldCommitAdvance(first.NewStatus, registrationFailed: true));

        var retry = RGBInvoiceListener.EvaluateInvoiceState(invoice, inFlight);
        Assert.Equal(RGBInvoiceStatus.WaitingConfirmations, retry.NewStatus);
        Assert.Equal(PaymentStatus.Processing, retry.PaymentStatus);
        Assert.Single(retry.PaymentsToRecord);
        Assert.Equal(1, retry.PaymentsToRecord[0].Idx);
    }

    [Fact]
    public void WaitingConfirmations_CommittedAfterAFailure_ProducesNoFurtherPaymentWork()
    {
        // The defect this fix closes, pinned as the counterfactual: had the advance been committed,
        // the same transfer yields no status and no payments, so nothing retries and nothing alarms.
        var advanced = new RGBInvoice
        {
            Id = "inv-1", WalletId = "w", RecipientId = "r", AssetId = "a",
            Amount = 100, Status = RGBInvoiceStatus.WaitingConfirmations
        };

        var result = RGBInvoiceListener.EvaluateInvoiceState(
            advanced, new[] { new RgbTransfer { Idx = 1, Status = 2, Amount = 100, Txid = "tx" } });

        Assert.Null(result.NewStatus);
        Assert.Null(result.PaymentStatus);
        Assert.Empty(result.PaymentsToRecord);
    }

    [Fact]
    public void AHeldWaitingConfirmationsInvoice_StillSettlesWhenTheTransferConfirms()
    {
        // Holding the row must not cost the settlement: the settled branch keys off "not Settled",
        // not off WaitingConfirmations, so a held invoice settles with the full cumulative amount.
        var held = new RGBInvoice
        {
            Id = "inv-1", WalletId = "w", RecipientId = "r", AssetId = "a",
            Amount = 100, Status = RGBInvoiceStatus.Pending
        };

        var result = RGBInvoiceListener.EvaluateInvoiceState(
            held, new[] { new RgbTransfer { Idx = 1, Status = 3, Amount = 100, Txid = "tx" } });

        Assert.Equal(RGBInvoiceStatus.Settled, result.NewStatus);
        Assert.Equal(PaymentStatus.Settled, result.PaymentStatus);
        Assert.Equal(100, result.ReceivedAmount);
    }

    [Fact] // G2-T9 and G2-T11
    public void ShouldRepublishOnAlreadyRecorded_IsBoundedToSettled()
    {
        Assert.True(RGBInvoiceListener.ShouldRepublishOnAlreadyRecorded(PaymentStatus.Settled));

        // An underpaid invoice stays in the sweep filter forever; republishing its already-Processing
        // payment would emit an event every ten seconds for the life of the invoice.
        Assert.False(RGBInvoiceListener.ShouldRepublishOnAlreadyRecorded(PaymentStatus.Processing));
    }

    [Fact] // G2-T12 — the blocker case: a failed insert must never satisfy the gate
    public void ClassifyNullAddPayment_WithTheInvoicePresentAndThePaymentAbsent_IsFailed()
    {
        var after = InvoiceWith("rgb:other:1");

        var outcome = RGBInvoiceListener.ClassifyNullAddPayment(after, new PaymentPrompt(), "rgb:me:0");

        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Failed, outcome);
    }

    [Fact] // G2-T13 — the duplicate-is-Recorded and the invoice-absent-is-Declined outcomes
    public void ClassifyNullAddPayment_DuplicateIsRecorded_AbsentInvoiceIsDeclined()
    {
        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Recorded,
            RGBInvoiceListener.ClassifyNullAddPayment(InvoiceWith("rgb:me:0"), new PaymentPrompt(), "rgb:me:0"));

        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Declined,
            RGBInvoiceListener.ClassifyNullAddPayment(null, null, "rgb:me:0"));
    }

    [Fact]
    public void ClassifyNullAddPayment_MissingPrompt_MustBeUnregisterableNotDecline()
    {
        var body = RoslynPins.BodyOf(Listener("ClassifyNullAddPayment"));
        var branch = IfWithCondition(body, "prompt is null");

        var outcome = ReturnedRegistration(branch);
        Assert.True(outcome == "PaymentRegistration.Unregisterable",
            $"a re-observed missing prompt can never be registered on retry, so this branch must classify "
            + $"Unregisterable and let ProcessTransfers close the invoice terminally Failed; Declined here "
            + $"lets it close Settled with the received asset never credited to any BTCPay payment, "
            + $"found 'return {outcome};'");
    }

    [Fact]
    public void RecordOrUpdatePayment_AbsentInvoice_StaysDeclined()
    {
        var body = RoslynPins.BodyOf(Listener("RecordOrUpdatePayment"));
        var branch = IfWithCondition(body, "invoiceEntity == null");

        var outcome = ReturnedRegistration(branch);
        Assert.True(outcome == "PaymentRegistration.Declined",
            $"an absent BTCPay invoice has nothing to ever credit, so this branch must stay Declined, "
            + $"found 'return {outcome};'");
    }

    [Fact]
    public void RecordOrUpdatePayment_StoreMismatch_MustBeUnregisterableNotDecline()
    {
        var body = RoslynPins.BodyOf(Listener("RecordOrUpdatePayment"));
        var branch = IfWithCondition(body,
            "!RGBPaymentMethodHandler.WalletBelongsToStore(invoiceEntity.StoreId, expectedStoreId)");

        var outcome = ReturnedRegistration(branch);
        Assert.True(outcome == "PaymentRegistration.Unregisterable",
            $"a store mismatch can never be registered on retry, so this branch must classify "
            + $"Unregisterable and let ProcessTransfers close the invoice terminally Failed; Declined here "
            + $"lets it close Settled while the received asset sits uncredited forever, "
            + $"found 'return {outcome};'");
    }

    [Fact]
    public void RecordOrUpdatePayment_MissingPrompt_MustBeUnregisterableNotDecline()
    {
        var body = RoslynPins.BodyOf(Listener("RecordOrUpdatePayment"));
        var branch = IfWithCondition(body, "prompt == null");

        var outcome = ReturnedRegistration(branch);
        Assert.True(outcome == "PaymentRegistration.Unregisterable",
            $"a missing RGB payment prompt can never be registered on retry, so this branch must classify "
            + $"Unregisterable and let ProcessTransfers close the invoice terminally Failed; Declined here "
            + $"lets it close Settled while the received asset sits uncredited forever, "
            + $"found 'return {outcome};'");
    }

    [Fact]
    public void ClassifyPromptPricingIdentity_AssetMismatch_MustBeUnregisterableNotFailed()
    {
        var body = RoslynPins.BodyOf(Listener("ClassifyPromptPricingIdentity"));
        var branch = IfWithCondition(body, "!IsAssetMatch(rgbInvoice.AssetId, details.AssetId ?? \"\")");

        var outcome = ReturnedRegistration(branch);
        Assert.True(outcome == "PaymentRegistration.Unregisterable",
            $"an asset-id mismatch between the persisted RGB invoice and the prompt blob is a pure "
            + $"function of already-persisted data and can never change on retry, so this branch must "
            + $"classify Unregisterable and let ProcessTransfers close the invoice terminally Failed; "
            + $"Failed here holds the invoice at its previous status forever under a sentence that "
            + $"promises a retry that can never succeed, found 'return {outcome};'");
    }

    [Fact]
    public void ClassifyPromptPricingIdentity_InvalidPricingCode_MustBeUnregisterableNotFailed()
    {
        var body = RoslynPins.BodyOf(Listener("ClassifyPromptPricingIdentity"));
        var catchClause = body.DescendantNodes().OfType<CatchClauseSyntax>()
            .Where(c => c.Declaration?.Type.ToString() == "FormatException")
            .ToList();
        Assert.True(catchClause.Count == 1,
            $"expected exactly one 'catch (FormatException)', found {catchClause.Count}");

        var returns = catchClause[0].DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
        Assert.True(returns.Count == 1,
            $"expected exactly one return inside 'catch (FormatException)', found {returns.Count}");

        var outcome = returns[0].Expression!.ToString();
        Assert.True(outcome == "PaymentRegistration.Unregisterable",
            $"a FormatException out of ResolvePaymentCurrency is thrown from the same already-persisted "
            + $"prompt blob on every retry, so this branch must classify Unregisterable and let "
            + $"ProcessTransfers close the invoice terminally Failed; Failed here holds the invoice "
            + $"forever under a sentence that promises a retry that can never succeed, "
            + $"found 'return {outcome};'");
    }

    [Fact]
    public void RecordOrUpdatePayment_PricingIdentityRefusal_MustBeUnregisterableNotFailed()
    {
        var body = RoslynPins.BodyOf(Listener("RecordOrUpdatePayment"));
        var branch = IfWithCondition(body, "identity == PaymentRegistration.Unregisterable");

        var returns = branch.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
        Assert.True(returns.Count == 1,
            $"expected exactly one return from the pricing-identity refusal branch, found {returns.Count}");
        Assert.True(returns[0].Expression!.ToString() == "identity",
            $"the pricing-identity refusal must propagate the Unregisterable outcome outward so "
            + $"ProcessTransfers can close the invoice terminally Failed instead of holding it forever, "
            + $"found 'return {returns[0].Expression};'");
    }

    // ---- the wiring, pinned syntactically because ProcessTransfers cannot be driven ---------------

    [Fact] // G2-T1b — a blocked advance must leave the row entirely untouched
    public void TheHeldAdvance_PrecedesEveryEntityWrite()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var gate = GateCall(body);
        var guard = gate.Ancestors().OfType<IfStatementSyntax>().First();
        var jump = guard.DescendantNodes().OfType<ContinueStatementSyntax>().ToList();
        Assert.True(jump.Count == 1, $"the gate must skip the advance with continue, found {jump.Count}");
        var terminalBranch = IfWithCondition(body, "unregisterable");

        // All FOUR writes, not just inv.Status: comparing against one lets the other three be hoisted
        // above the guard, leaving a half-written row on a transition that was blocked.
        foreach (var field in new[] { "Status", "Txid", "ReceivedAmount", "SettledAt" })
        {
            var writesInsideGuard = guard.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.ValueText == field
                            && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "inv" })
                .ToList();
            Assert.True(writesInsideGuard.Count == 0,
                $"inv.{field} is assigned inside the held-advance guard before its continue, found "
                + $"{writesInsideGuard.Count} — a blocked advance would commit part of the transition");

            var writesAfterGuard = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.ValueText == field
                            && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "inv" }
                            && a.SpanStart > guard.Span.End)
                .ToList();
            Assert.True(writesAfterGuard.Count == 1,
                $"expected exactly one write to inv.{field} once the held-advance guard has fallen through, "
                + $"found {writesAfterGuard.Count}");

            var writesInTerminalBranch = terminalBranch.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.ValueText == field
                            && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "inv" })
                .ToList();
            var writesAnywhere = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.ValueText == field
                            && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "inv" })
                .ToList();
            Assert.True(
                writesAnywhere.Count == writesInTerminalBranch.Count + writesAfterGuard.Count,
                $"every write to inv.{field} must live either inside the terminal unregisterable branch "
                + $"or after the held-advance guard; {writesAnywhere.Count} write(s) exist but only "
                + $"{writesInTerminalBranch.Count + writesAfterGuard.Count} are accounted for, so one sits "
                + "before the guard and outside that branch, where a blocked advance commits part of the "
                + "transition — the exact half-written row the guard exists to prevent, and the hole the "
                + "inside-guard and after-guard counts alone cannot see");
        }
    }

    [Fact] // G2-T10(a) — the detection half; without it the whole fix ships inert
    public void ARegistrationThrow_SetsTheFailureFlag()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var catches = body.DescendantNodes().OfType<CatchClauseSyntax>()
            .Where(c => c.Ancestors().OfType<ForEachStatementSyntax>()
                .Any(f => f.Expression.ToString().Contains("PaymentsToRecord")))
            .ToList();
        Assert.True(catches.Count == 1,
            $"expected exactly one catch around the registration call, found {catches.Count}");
        AssertSetsFlag(catches[0]);
    }

    [Fact] // G2-T10(b) — the other half of detection
    public void AFailedRegistrationOutcome_SetsTheFailureFlag()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var comparisons = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("PaymentRegistration.Failed"))
            .ToList();
        Assert.True(comparisons.Count == 1,
            $"the Failed outcome must be compared exactly once, found {comparisons.Count}");
        AssertSetsFlag(comparisons[0]);
    }

    [Fact]
    public void AnUnregisterableRegistrationOutcome_SetsTheUnregisterableFlag()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var comparisons = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("PaymentRegistration.Unregisterable"))
            .ToList();
        Assert.True(comparisons.Count == 1,
            $"the Unregisterable outcome must be compared exactly once, found {comparisons.Count}");
        AssertSetsFlag(comparisons[0], "unregisterable");
    }

    [Fact]
    public void TheUnregisterableBranch_PrecedesAndBypassesTheAdvanceGate()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var gate = GateCall(body);
        var branch = IfWithCondition(body, "unregisterable");

        Assert.True(branch.SpanStart < gate.SpanStart,
            "the unregisterable branch must precede the ShouldCommitAdvance gate and continue before "
            + "reaching it, or a store-mismatched or promptless invoice would be subjected to the "
            + "hold-and-retry gate instead of closing terminally");

        var jump = branch.DescendantNodes().OfType<ContinueStatementSyntax>().ToList();
        Assert.True(jump.Count == 1,
            $"the unregisterable branch must terminate this invoice's processing with continue, found {jump.Count}");
    }

    [Fact]
    public void TheUnregisterableOutcome_RecordsTxidAndAmountForensicsAsTerminalFailed()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var branch = IfWithCondition(body, "unregisterable");
        var jump = branch.DescendantNodes().OfType<ContinueStatementSyntax>().Single();

        foreach (var field in new[] { "Status", "Txid", "ReceivedAmount" })
        {
            var writes = branch.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.ValueText == field
                            && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "inv" })
                .ToList();
            Assert.True(writes.Count == 1,
                $"an operator reading a Failed row needs inv.{field} recorded as forensics, found "
                + $"{writes.Count} write(s)");
            Assert.True(writes[0].SpanStart < jump.SpanStart,
                $"inv.{field} must be written before the branch continues, or the forensics are lost");
        }

        var statusWrite = branch.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .First(a => a.Left is MemberAccessExpressionSyntax m
                        && m.Name.Identifier.ValueText == "Status"
                        && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "inv" });
        Assert.True(statusWrite.Right.ToString() == "RGBInvoiceStatus.Failed",
            $"an unregisterable payment must close the invoice terminally Failed, not Settled, found "
            + $"'{statusWrite}'");
    }

    [Fact]
    public void TheUnregisterableOutcome_NeverClaimsNoPaymentWasCredited()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var branch = IfWithCondition(body, "unregisterable");

        var templates = branch.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Select(l => l.Token.ValueText)
            .Where(t => t.Contains("marked Failed", StringComparison.Ordinal))
            .ToList();
        Assert.True(templates.Count == 1,
            $"expected exactly one terminal-Failed log template in the branch, found {templates.Count}");

        foreach (var forbidden in new[]
                 { "not credited", "no BTCPay payment", "never credited", "neither created" })
        {
            Assert.True(!templates[0].Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"the terminal log must assert NOTHING about whether a BTCPay payment exists or was "
                + $"created by this attempt. It is emitted before any existing-payment lookup "
                + $"(ClassifyPromptPricingIdentity refuses at the pricing gate, which precedes "
                + $"GetPayments), so a legacy prompt can leave a Processing payment standing while the "
                + $"message denies it; and AddPayment returns null for any DbUpdateException while "
                + $"EnableRetryOnFailure(10) means an exception is not proof a write did not commit, so "
                + $"a no-write claim is not establishable either. An operator acting on either denial "
                + $"credits the same transfer twice; found '{forbidden}' in '{templates[0]}'");
        }

        Assert.True(templates[0].Contains("already recorded", StringComparison.Ordinal),
            $"the terminal log must send the operator to look for a payment already recorded on the "
            + $"BTCPay invoice, or the double-credit this wording exists to prevent is unguarded; found "
            + $"'{templates[0]}'");
    }

    [Fact]
    public void TheUnregisterableOutcome_NeverWritesSettledAt()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var branch = IfWithCondition(body, "unregisterable");

        var settledAtWrites = branch.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax m
                        && m.Name.Identifier.ValueText == "SettledAt"
                        && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "inv" })
            .ToList();
        Assert.True(settledAtWrites.Count == 0,
            $"inv.SettledAt must never be written on the unregisterable path — that would claim a "
            + $"credited settlement for a payment this invoice never registered, found "
            + $"{settledAtWrites.Count} write(s)");
    }

    [Fact]
    public void TheUnregisterableOutcome_LeavesPageSucceededUntouched()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var branch = IfWithCondition(body, "unregisterable");

        var pageSucceededWrites = branch.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "pageSucceeded" })
            .ToList();
        Assert.True(pageSucceededWrites.Count == 0,
            $"a terminally Failed invoice has nothing left to retry, so pageSucceeded must stay true here "
            + $"or CompleteRecovery(generation, false) keeps RefreshAllWallets running on every poll pass "
            + $"forever; found {pageSucceededWrites.Count} write(s)");
    }

    [Fact] // G2-T10(c) — the gate is called, and called with the real arguments
    public void TheAdvanceGate_IsCalledWithTheLiveStatusAndTheLiveFlag()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var gate = GateCall(body);
        var arguments = gate.ArgumentList.Arguments;
        Assert.True(arguments.Count == 2, $"expected (newStatus, registrationFailed), found {arguments.Count}");

        var status = Assert.IsType<MemberAccessExpressionSyntax>(arguments[0].Expression);
        Assert.Equal("NewStatus", status.Name.Identifier.ValueText);

        // A literal `false` here keeps every test and pin green while committing Settled after a
        // failed registration — G2 reproduced inside G2's own fix.
        Assert.True(arguments[1].Expression is IdentifierNameSyntax { Identifier.ValueText: "registrationFailed" },
            $"the second argument must be the live flag, found '{arguments[1]}'");
    }

    [Fact] // G2-T10(d) — the bounded republish, with its argument pinned
    public void TheAlreadyRecordedBranch_RepublishesThroughTheBoundedCondition()
    {
        var body = RoslynPins.BodyOf(Listener("RecordOrUpdatePayment"));
        var call = SingleCall(body, "ShouldRepublishOnAlreadyRecorded");
        Assert.True(call.ArgumentList.Arguments.Count == 1);

        // A literal PaymentStatus.Settled satisfies this clause and G2-T9/T11 while republishing
        // already-Processing payments on every poll forever.
        Assert.True(call.ArgumentList.Arguments[0].Expression
                is IdentifierNameSyntax { Identifier.ValueText: "targetStatus" },
            $"the republish must be decided from the live target status, found '{call.ArgumentList.Arguments[0]}'");

        var guard = call.Ancestors().OfType<IfStatementSyntax>().First();
        AssertPublishesNeedUpdate(guard);
    }

    [Fact] // G2-T10(e) — a genuine duplicate must still ask BTCPay to re-derive
    public void TheClassifiedDuplicate_PublishesNeedUpdate()
    {
        var body = RoslynPins.BodyOf(Listener("RecordOrUpdatePayment"));
        var classify = SingleCall(body, "ClassifyNullAddPayment");
        var branch = classify.Ancestors().OfType<BlockSyntax>().First();

        var recorded = branch.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("PaymentRegistration.Recorded"))
            .ToList();
        Assert.True(recorded.Count == 1,
            "the null-AddPayment path must publish when the classifier says Recorded; without it a "
            + $"genuine duplicate advances Settled with BTCPay never re-deriving, found {recorded.Count}");
        AssertPublishesNeedUpdate(recorded[0]);
    }

    [Fact] // G2-T15 — seam F wiring
    public void TheNullAddPaymentPath_RoutesThroughTheClassifier()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var method = Listener("RecordOrUpdatePayment");
        RoslynPins.AssertNoLocalShadow(method, "ClassifyNullAddPayment");

        var call = SingleCall(RoslynPins.BodyOf(method), "ClassifyNullAddPayment");
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, call));
        Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.RGBInvoiceListener",
            symbol.ContainingType.ToDisplayString());

        // Re-queried state, not the stale entity the failed insert was built against.
        Assert.True(call.ArgumentList.Arguments.Count == 3);
        Assert.Equal("paymentId", call.ArgumentList.Arguments[2].Expression.ToString());
    }

    // ---- helpers ---------------------------------------------------------------------------------

    static InvoiceEntity InvoiceWith(params string[] paymentIds)
    {
        var invoice = new InvoiceEntity();
        // GetPayments reads this obsolete collection, and it is the only way to seed it from a test.
#pragma warning disable CS0618
        invoice.Payments = paymentIds
            .Select(id => new PaymentEntity { Id = id, Status = PaymentStatus.Settled })
            .ToList();
#pragma warning restore CS0618
        return invoice;
    }

    static MethodDeclarationSyntax Listener(string method) =>
        RoslynPins.Method(PluginCompilation.Shared.Tree(ListenerFile), "RGBInvoiceListener", method);

    static InvocationExpressionSyntax GateCall(SyntaxNode body) => SingleCall(body, "ShouldCommitAdvance");

    static IfStatementSyntax IfWithCondition(SyntaxNode body, string condition)
    {
        var matches = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString() == condition)
            .ToList();
        Assert.True(matches.Count == 1, $"expected exactly one 'if ({condition})', found {matches.Count}");
        return matches[0];
    }

    static string ReturnedRegistration(IfStatementSyntax ifStatement)
    {
        var returns = ifStatement.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
        Assert.True(returns.Count == 1,
            $"expected exactly one return inside 'if ({ifStatement.Condition})', found {returns.Count}");
        return returns[0].Expression!.ToString();
    }

    static InvocationExpressionSyntax SingleCall(SyntaxNode body, string name)
    {
        var matches = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == name)
            .ToList();
        Assert.True(matches.Count == 1, $"expected exactly one call to '{name}', found {matches.Count}");
        return matches[0];
    }

    static void AssertSetsFlag(SyntaxNode scope, string flagName = "registrationFailed")
    {
        var sets = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax id && id.Identifier.ValueText == flagName
                        && a.Right.ToString() == "true")
            .ToList();
        Assert.True(sets.Count == 1,
            $"{flagName} must be set here, found {sets.Count} assignment(s) — the gate is a pure "
            + "function of this flag, so an unset flag makes the entire fix inert");
    }

    static void AssertPublishesNeedUpdate(SyntaxNode scope)
    {
        var published = scope.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Count(o => o.Type.ToString().EndsWith("InvoiceNeedUpdateEvent", StringComparison.Ordinal));
        Assert.True(published == 1,
            $"this branch must publish InvoiceNeedUpdateEvent exactly once, found {published}");
    }
}
