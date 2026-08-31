using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbConfirmationLookupTests
{
    static readonly Network Net = Network.RegTest;

    sealed class FakeChain : IBitcoinChainClient
    {
        public Func<string, string> RawTx = _ => throw new InvalidOperationException("no raw tx");
        public Func<Script, IReadOnlyList<UnspentWithConfirmation>> Rows = _ => [];
        public int RawTxCalls;
        public readonly List<Script> ScriptsQueried = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default)
        {
            RawTxCalls++;
            return Task.FromResult(RawTx(txid));
        }

        public Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UnspentWithConfirmation>> ListUnspentWithConfirmationByScriptAsync(
            Script script, CancellationToken ct = default)
        {
            ScriptsQueried.Add(script);
            return Task.FromResult(Rows(script));
        }

        public void Dispose() { }
    }

    static Script NewScript() =>
        new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;

    static Transaction ParentWith(params Script[] outputScripts)
    {
        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        foreach (var s in outputScripts)
            tx.Outputs.Add(new TxOut(Money.Satoshis(10_000), s));
        return tx;
    }

    static Task<bool?> Ask(FakeChain chain, Transaction parent, int vout) =>
        RGBWalletService.ConfirmationOfAsync(
            chain, new Dictionary<string, Transaction>(), new HashSet<Script>(),
            new Dictionary<(string Txid, int Vout), bool>(),
            new Outpoint(parent.GetHash().ToString(), vout), Net, CancellationToken.None);

    [Fact]
    public async Task AnIndexerRowMarkedConfirmedForThatExactOutpointReportsConfirmed()
    {
        var script = NewScript();
        var parent = ParentWith(script);
        var chain = new FakeChain
        {
            RawTx = _ => parent.ToHex(),
            Rows = _ => [new UnspentWithConfirmation(new Outpoint(parent.GetHash().ToString(), 0), true)]
        };

        Assert.True(await Ask(chain, parent, 0));
    }

    [Fact]
    public async Task AnIndexerRowMarkedUnminedForThatExactOutpointReportsUnconfirmed()
    {
        var script = NewScript();
        var parent = ParentWith(script);
        var chain = new FakeChain
        {
            RawTx = _ => parent.ToHex(),
            Rows = _ => [new UnspentWithConfirmation(new Outpoint(parent.GetHash().ToString(), 0), false)]
        };

        var answer = await Ask(chain, parent, 0);

        Assert.True(answer == false,
            "The send path's confirmation lookup must report an unmined output as unconfirmed. If it "
            + "reported confirmed, or ignored the indexer and answered from nothing, an output whose "
            + "parent can still be replaced would be selected and signed.");
    }

    [Fact]
    public async Task TheIndexerIsAskedAboutTheScriptOfTheRequestedVoutNotTheFirstOutput()
    {
        var first = NewScript();
        var second = NewScript();
        var parent = ParentWith(first, second);
        var chain = new FakeChain { RawTx = _ => parent.ToHex() };

        await Ask(chain, parent, 1);

        Assert.Single(chain.ScriptsQueried);
        Assert.True(chain.ScriptsQueried[0] == second,
            "Confirmation must be looked up against the script of the outpoint's own vout. Querying "
            + "another output's script answers about a different coin, so an unmined output could be "
            + "reported confirmed by a sibling that was already mined.");
    }

    [Fact]
    public async Task AnOutpointTheIndexerNoLongerListsIsUnknownRatherThanConfirmed()
    {
        var parent = ParentWith(NewScript());
        var chain = new FakeChain { RawTx = _ => parent.ToHex(), Rows = _ => [] };

        Assert.Null(await Ask(chain, parent, 0));
    }

    [Fact]
    public async Task AVoutBeyondTheParentsOutputsIsUnknownAndCostsNoIndexerCall()
    {
        var parent = ParentWith(NewScript());
        var chain = new FakeChain { RawTx = _ => parent.ToHex() };

        var answer = await Ask(chain, parent, 5);

        Assert.True(answer is null,
            "A vout past the parent's output count must fail closed as unknown; indexing the array "
            + "directly would reach the operator as an ArgumentOutOfRangeException they cannot act on.");
        Assert.Empty(chain.ScriptsQueried);
    }

    [Fact]
    public async Task AParentThatDoesNotHashToTheRequestedTxidIsRefused()
    {
        var parent = ParentWith(NewScript());
        var impostor = ParentWith(NewScript(), NewScript());
        var chain = new FakeChain { RawTx = _ => impostor.ToHex() };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Ask(chain, parent, 0));

        Assert.Contains("wrong txid", ex.Message);
    }

    [Fact]
    public async Task TwoOutputsOfOneParentCostASingleFetch()
    {
        var parent = ParentWith(NewScript(), NewScript());
        var chain = new FakeChain { RawTx = _ => parent.ToHex() };
        var cache = new Dictionary<string, Transaction>();
        var asked = new HashSet<Script>();
        var answers = new Dictionary<(string Txid, int Vout), bool>();
        var txid = parent.GetHash().ToString();

        await RGBWalletService.ConfirmationOfAsync(
            chain, cache, asked, answers, new Outpoint(txid, 0), Net, CancellationToken.None);
        await RGBWalletService.ConfirmationOfAsync(
            chain, cache, asked, answers, new Outpoint(txid, 1), Net, CancellationToken.None);

        Assert.True(chain.RawTxCalls == 1,
            $"The parent transaction must be fetched once per send, not once per examined output; "
            + $"found {chain.RawTxCalls} fetches. A wallet whose UTXOs share parents would otherwise "
            + "re-download the same transaction for every candidate the walk examines.");
    }

    [Fact]
    public async Task ManyDepositsToOneReusedAddressCostOneParentFetchAndOneIndexerQuery()
    {
        var reused = NewScript();
        var deposits = Enumerable.Range(0, 200)
            .Select(i => ParentWith(NewScript(), reused, new Script(new byte[] { (byte)(i % 251) })))
            .ToList();
        var rows = deposits
            .Select(d => new UnspentWithConfirmation(new Outpoint(d.GetHash().ToString(), 1), false))
            .ToList();
        var byTxid = deposits.ToDictionary(d => d.GetHash().ToString(), d => d.ToHex());
        var chain = new FakeChain { RawTx = txid => byTxid[txid], Rows = _ => rows };
        var cache = new Dictionary<string, Transaction>();
        var asked = new HashSet<Script>();
        var answers = new Dictionary<(string Txid, int Vout), bool>();

        foreach (var deposit in deposits)
            await RGBWalletService.ConfirmationOfAsync(
                chain, cache, asked, answers,
                new Outpoint(deposit.GetHash().ToString(), 1), Net, CancellationToken.None);

        Assert.True(chain.ScriptsQueried.Count == 1 && chain.RawTxCalls == 1,
            $"Deposits paid to one reused address must cost one parent fetch and one indexer query "
            + $"however many there are; found {chain.RawTxCalls} fetches and "
            + $"{chain.ScriptsQueried.Count} queries. One query already returns every unspent output "
            + "on that address, so answering the rest from it is what stops anyone who has seen a "
            + "wallet address from making each deposit cost a round trip while the send lock is held.");
    }

    [Fact]
    public async Task AnAnswerCachedForOneOutpointIsNeverReusedForAnother()
    {
        var script = NewScript();
        var parent = ParentWith(script, script);
        var txid = parent.GetHash().ToString();
        var chain = new FakeChain
        {
            RawTx = _ => parent.ToHex(),
            Rows = _ =>
            [
                new UnspentWithConfirmation(new Outpoint(txid, 0), true),
                new UnspentWithConfirmation(new Outpoint(txid, 1), false)
            ]
        };
        var cache = new Dictionary<string, Transaction>();
        var asked = new HashSet<Script>();
        var answers = new Dictionary<(string Txid, int Vout), bool>();

        var first = await RGBWalletService.ConfirmationOfAsync(
            chain, cache, asked, answers, new Outpoint(txid, 0), Net, CancellationToken.None);
        var second = await RGBWalletService.ConfirmationOfAsync(
            chain, cache, asked, answers, new Outpoint(txid, 1), Net, CancellationToken.None);

        Assert.True(first == true, "Vout 0 is mined and must read as confirmed.");
        Assert.True(second == false,
            "Vout 1 shares a script and a transaction with vout 0 but is not mined. Caching answers "
            + "per outpoint must not let one output's confirmation stand in for another's, or an "
            + "unmined output would be selected because a sibling was mined.");
    }

    [Fact]
    public void TwoSeparatelyBuiltCopiesOfOneScriptAreTheSameDictionaryKey()
    {
        var original = NewScript();
        var a = new Script(original.ToBytes());
        var b = new Script(original.ToBytes());
        var map = new Dictionary<Script, int> { [a] = 1 };

        Assert.False(ReferenceEquals(a, b));
        Assert.True(map.ContainsKey(b),
            "The send path memoizes indexer lookups in a Dictionary keyed by Script, and in a real "
            + "send every script is a fresh object parsed out of the parent transaction. If Script "
            + "ever stopped comparing by value, that memo would never hit and every examined output "
            + "would silently cost a round trip again.");
    }

    [Fact]
    public async Task ManyOutputsPaidToOneAddressCostASingleIndexerQuery()
    {
        var reused = NewScript();
        var parent = ParentWith(reused, reused, reused, reused);
        var chain = new FakeChain { RawTx = _ => parent.ToHex() };
        var cache = new Dictionary<string, Transaction>();
        var asked = new HashSet<Script>();
        var answers = new Dictionary<(string Txid, int Vout), bool>();
        var txid = parent.GetHash().ToString();

        for (var vout = 0; vout < parent.Outputs.Count; vout++)
            await RGBWalletService.ConfirmationOfAsync(
                chain, cache, asked, answers, new Outpoint(txid, vout), Net, CancellationToken.None);

        Assert.True(chain.ScriptsQueried.Count == 1,
            $"Confirmation for outputs sharing one script must cost one indexer query, not one per "
            + $"output; found {chain.ScriptsQueried.Count}. Anyone who has seen a wallet address can "
            + "pay it many outputs in a single transaction, and without this the walk makes one "
            + "round trip per output while the send holds that wallet's send lock.");
    }
}
