using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// The ingress predicate for the invoice listener (audit finding H2b). The listener subscribed to every
/// invoice created anywhere on the server; this predicate is what makes the subscription RGB-scoped.
/// It is deliberately BROADER than the check CheckSingleInvoice applies: prompt presence, not Details.
/// A false TRUE costs a bounded queue slot and a database round-trip; a false FALSE gives
/// up the one window in which a lazily-activated invoice can still be processed from the queue — between
/// Created and that entry draining — leaving it to the sweep, which a failing durability flush can skip.
/// </summary>
public class RgbListenerIngressFilterTests
{
    // 1 — nothing to enqueue: no RGB prompt at all, either shape.
    [Fact]
    public void ShouldEnqueue_IsFalse_WhenTheInvoiceHasNoRgbPrompt()
    {
        Assert.False(RGBInvoiceListener.ShouldEnqueue(new InvoiceEntity()));
        Assert.False(RGBInvoiceListener.ShouldEnqueue(
            InvoiceWithPrompt(new PaymentMethodId("BTC-CHAIN"), Details(),
                id: "inv-btc", currency: "USD", promptCurrency: "BTC", divisibility: 8,
                storeId: "store-a", destination: "bc1qbtc")));
    }

    // 2 — the lazy-activation shape: the RGB prompt exists, is NOT activated (Inactive = true) and has no
    //     Details.  Both halves matter — see InvoiceWithPrompt's comment.
    // Its prompt carries the INVOICE's currency and divisibility 0 because that is what production builds
    // before activation (`BeforeFetchingRates`); `ConfigurePrompt` overwrites both only on activation.
    // A fixture that models this state wrongly lets a predicate keyed on the currency relation through.
    // It MUST still be enqueued. Such an invoice gains Details later without republishing Created, and the
    // drain re-fetches before deciding — so the queue entry is what processes it. Filtering on Details here
    // would drop it, and on a wallet the periodic sweep skips that is a payment never processed at all.
    [Fact]
    public void ShouldEnqueue_IsTrue_WhenTheRgbPromptExistsButIsNotYetActivated()
    {
        Assert.True(RGBInvoiceListener.ShouldEnqueue(
            InvoiceWithPrompt(RGBPlugin.RGBPaymentMethodId, details: null, inactive: true,
                id: "inv-lazy", currency: "EUR", promptCurrency: "EUR", divisibility: 0,
                storeId: "store-b", destination: null, type: InvoiceType.TopUp,
                price: 0m, lazyPaymentMethods: true,
                status: InvoiceStatus.Processing, archived: true,
                // No order id, an invoice created at a different hour, and an expiry already in the past:
                // production produces all three, and a predicate keyed on any of them rejects real invoices.
                orderId: null, invoiceTimeHour: 3, expiresInSeconds: -600, version: 1)));
    }

    // 3 — the ordinary activated shape, and deliberately a MULTI-PROMPT invoice. Production invoices carry a
    // prompt per enabled payment method (UIInvoiceController attaches them all before Created), so a
    // single-prompt fixture would let `GetPaymentPrompts().SingleOrDefault()?.PaymentMethodId == …` pass every
    // test and then THROW on an ordinary RGB+BTC invoice — permanently losing it on a wallet the sweep skips.
    [Fact]
    public void ShouldEnqueue_IsTrue_WhenTheRgbPromptCarriesDetails()
    {
        Assert.True(RGBInvoiceListener.ShouldEnqueue(
            WithExtraPrompt(
                InvoiceWithPrompt(RGBPlugin.RGBPaymentMethodId, Details(),
                    // Divisibility 18 against test 2's 0: RGB asset precision is a u8, so a predicate
                    // clamping it to Bitcoin's 8 rejects a real high-precision asset, and one requiring
                    // it to be positive rejects the precision-0 assets this plugin issues by default.
                    id: "inv-active", currency: "JPY", promptCurrency: "RGB2", divisibility: 18,
                    storeId: "store-c", destination: "rgb:active", price: 42.5m,
                    status: InvoiceStatus.Settled, speedPolicy: SpeedPolicy.LowSpeed,
                    orderId: "order-active", invoiceTimeHour: 23, expiresInSeconds: 86400),
                new PaymentMethodId("BTC-CHAIN"))));
    }

    // 4 — the values no must-enqueue fixture carried. Measured: with tests 1–3 alone, four wrong
    // implementations survived all three — `… && inv.Currency != "USD"`, `… && inv.StoreId != "store-a"`,
    // `… && p.Divisibility != 8` and `… && inv.Id.StartsWith("inv-")` — each permanently rejecting a class
    // of real RGB invoices. A conjunct is only exercised where the predicate must return TRUE, so varying
    // the negatives cannot reach any of them: the first three take the negative fixture's currency, store
    // and divisibility, and the fourth takes an id that does NOT begin `inv-`, which every earlier fixture
    // did — negative and positive alike.
    // The RGB prompt is appended LAST here on purpose. Test 3 puts it first, so between them the fixtures
    // pin both prompt orders: `GetPaymentPrompts().FirstOrDefault()?.PaymentMethodId == RGB` survived tests
    // 1–3 because the only multi-prompt fixture happened to enumerate RGB first, and PaymentPromptDictionary
    // wraps a plain Dictionary — production guarantees no order at all.
    [Fact]
    public void ShouldEnqueue_IsTrue_ForTheValuesNoMustEnqueueFixtureCarried()
    {
        Assert.True(RGBInvoiceListener.ShouldEnqueue(
            WithRgbPromptLast(
                InvoiceWithPrompt(new PaymentMethodId("BTC-CHAIN"), Details(),
                    id: "checkout-4", currency: "USD", promptCurrency: "BTC", divisibility: 8,
                    storeId: "store-a", destination: "bc1qcheckout", price: 7m,
                    orderId: "", invoiceTimeHour: 0, expiresInSeconds: 60,
                    speedPolicy: SpeedPolicy.HighSpeed),
                promptCurrency: "RGB1", divisibility: 8, destination: "rgb:checkout-4")));
    }

    static JToken Details() => JToken.FromObject(new RGBPromptDetails
    {
        WalletId = "wallet-1",
        RgbInvoiceId = "rgb-inv-1",
        RecipientId = "recipient-1"
    });

    /// <summary>Appends the RGB prompt after an existing one, so the fixtures cover both prompt orders.</summary>
    static InvoiceEntity WithRgbPromptLast(InvoiceEntity invoice, string promptCurrency, int divisibility,
        string destination)
    {
        invoice.SetPaymentPrompt(RGBPlugin.RGBPaymentMethodId, new PaymentPrompt
        {
            Currency = promptCurrency, Divisibility = divisibility, Destination = destination, Details = Details()
        });
        return invoice;
    }

    /// <summary>Attaches a second, non-RGB prompt — production invoices are routinely multi-prompt.</summary>
    static InvoiceEntity WithExtraPrompt(InvoiceEntity invoice, PaymentMethodId other)
    {
        invoice.SetPaymentPrompt(other, new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = "bc1qextra", Details = Details()
        });
        return invoice;
    }

    // `inactive` is load-bearing, not decoration: PaymentPrompt.Inactive defaults to FALSE, so a prompt
    // built without it reports Activated == true. Test 2 would then pass against an implementation of the
    // form `p != null && p.Activated`, which rejects exactly the lazily-activated invoices this predicate
    // exists to admit — reinstating the permanent-false-REJECT path on wallets the sweep skips. The test
    // must build the state it claims to test.
    //
    // Every incidental field VARIES across the fixtures on purpose — including `Status` and `Archived`,
    // because `004.MonitoredInvoices.sql` deliberately returns non-`New` invoices that still have a pending
    // RGB payment, so a predicate keying on `Status == New` would drop real in-flight payments; including
    // `Type`, because a TopUp
    // invoice can carry a perfectly valid RGB prompt (`RgbPricingPlan` prices them), so a predicate keying
    // on `Type == Standard` would reject real payments; and including the RELATION between them.
    // The relation has to VARY, not merely differ from an earlier draft, and getting that wrong cost two
    // rounds. An early draft passed one `currency` to both invoice and prompt, so `prompt.Currency ==
    // inv.Currency` held in every fixture and a predicate keying on it passed. The correction made the two
    // differ everywhere — which flipped the invariant instead of breaking it, so `prompt.Currency !=
    // inv.Currency` then passed, and THAT predicate rejects every lazily-activated RGB invoice.
    // Both directions are now covered, and by modelling production rather than by picking values:
    // `BeforeFetchingRates` sets `Prompt.Currency = ctx.InvoiceEntity.Currency` and leaves `Divisibility`
    // at 0, so test 2's pre-activation prompt carries the invoice's own currency and divisibility 0;
    // `ConfigurePrompt` later overwrites both with the asset's, so tests 3 and 4 differ. When they were
    // all held constant, an
    // implementation like `GetPaymentPrompt(...) != null && inv.Currency == "USD"` passed all three tests
    // while permanently rejecting every non-USD RGB invoice — and RGB prices in whatever currency the store
    // configures. A test suite that pins a predicate must vary everything the predicate must NOT depend on,
    // and vary it across the fixtures where the predicate must return TRUE — a conjunct is invisible
    // anywhere else. Varying it across the positives only is what test 4 exists to finish; measured, four
    // wrong implementations survived tests 1-3 for exactly that reason.
    //
    // WHAT THESE TESTS DO AND DO NOT PIN. Stated as narrowly as the evidence allows, because the broader
    // versions of this paragraph have now been measured false twice: "only the shipped predicate survives"
    // (13 of 42 candidates survived) and "every candidate that would permanently reject a real RGB invoice
    // now dies" (five more survived, keyed on fields every fixture held constant).
    // What IS true: no field these fixtures set is constant across the must-enqueue cases — currency, prompt
    // currency, divisibility, store, destination, type, price, lazy flag, status, archived, speed policy,
    // version, order id, invoice time, expiry and prompt ORDER all vary. That kills every predicate keyed on
    // one of them, which is the family a maintainer plausibly writes.
    // What is NOT: a field nobody thought to set cannot be varied, so no test file closes this space. And
    // several candidates still pass on purpose — conjuncts always true in production, pure wideners that
    // cost a queue slot and reject nothing, and spellings equivalent under the invariant that an activated
    // prompt has Details. Recorded as R13 in the spec rather than claimed closed.
    // DEFAULTS ARE A FIELD VECTOR TOO, and this cost a round of its own. Every fixture left `Metadata`,
    // `InvoiceTime`, `Version` and `SpeedPolicy` at their CLR defaults, which production never produces —
    // so `… && inv.Metadata == null`, `&& inv.InvoiceTime == default`, `&& inv.Version == 0` and
    // `&& inv.SpeedPolicy == default` all passed every test while rejecting every real invoice. A field
    // nobody thought to set is indistinguishable, to a test, from a field deliberately held constant.
    static InvoiceEntity InvoiceWithPrompt(PaymentMethodId paymentMethodId, JToken? details,
        bool inactive = false, string id = "btcpay-inv-1", string currency = "USD",
        string promptCurrency = "RGB0", int divisibility = 8, string storeId = "store-1",
        string? destination = "dest-1", InvoiceType type = InvoiceType.Standard,
        decimal price = 1m, bool lazyPaymentMethods = false,
        InvoiceStatus status = InvoiceStatus.New, bool archived = false,
        SpeedPolicy speedPolicy = SpeedPolicy.MediumSpeed, int version = InvoiceEntity.Lastest_Version,
        string? orderId = "order-1", int invoiceTimeHour = 12, int expiresInSeconds = 3600)
    {
        var invoiceTime = new DateTimeOffset(2026, 8, 17, invoiceTimeHour, 0, 0, TimeSpan.Zero);
        var invoice = new InvoiceEntity
        {
            Id = id, Currency = currency, StoreId = storeId, Type = type,
            Metadata = new InvoiceMetadata { OrderId = orderId },
            InvoiceTime = invoiceTime,
            ExpirationTime = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
            Version = version, SpeedPolicy = speedPolicy,
            Price = price, LazyPaymentMethods = lazyPaymentMethods,
            Status = status, Archived = archived
        };
        invoice.SetPaymentPrompt(paymentMethodId, new PaymentPrompt
        {
            Currency = promptCurrency,
            Divisibility = divisibility,
            Destination = destination!,
            Inactive = inactive,
            Details = details!
        });
        return invoice;
    }
}
