using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// The guard refuses any PSBT input that is not provably owned by rgb-lib's VANILLA keychain, on the
// two paths that sign a PSBT they did not build. Note which account that is: rgb-lib's vanilla
// keychain is m/86'/coin'/0' — the signer member confusingly named _coloredAccountKey — while RGB
// allocations live on m/86'/82716x'/0'. The BIP84 account the signer also derives is vestigial.
public class RgbVanillaKeychainInputGuardTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    static readonly Network Net = Network.RegTest;

    static ExtKey Master() => new Mnemonic(TestMnemonic).DeriveExtKey();

    const string RgbVanillaAccount = "m/86'/1'/0'";
    const string RgbColoredAccount = "m/86'/827167'/0'";
    const string Bip84Account = "m/84'/1'/0'";

    static PubKey Pub(string account, uint idx) =>
        Master().Derive(new KeyPath($"{account}/0/{idx}")).GetPublicKey();

    static Script Taproot(string account, uint idx = 0) =>
        Pub(account, idx).GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;

    static Script Segwit(string account, uint idx = 0) =>
        Pub(account, idx).GetAddress(ScriptPubKeyType.Segwit, Net).ScriptPubKey;

    static Script ChangeScript => Taproot(RgbVanillaAccount, 9);

    // Funding tx carries a dummy input: PSBT.ToBase64() refuses to serialise a NonWitnessUtxo whose
    // transaction has no inputs.
    static (Transaction Funding, PSBT Psbt) Build(params Script[] inputScripts)
    {
        var funding = Transaction.Create(Net);
        funding.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        foreach (var s in inputScripts)
            funding.Outputs.Add(Money.Satoshis(100_000), s);

        var tx = Transaction.Create(Net);
        for (int i = 0; i < inputScripts.Length; i++)
            tx.Inputs.Add(new OutPoint(funding, (uint)i), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(99_000 * inputScripts.Length), ChangeScript);

        var psbt = PSBT.FromTransaction(tx, Net);
        for (int i = 0; i < inputScripts.Length; i++)
            psbt.Inputs[i].WitnessUtxo = funding.Outputs[i];
        return (funding, psbt);
    }

    static SigningPolicy Policy(bool guard) => new()
    {
        RequireRgbVanillaKeychainInputs = guard,
        AllowedScripts = new HashSet<Script> { ChangeScript },
        MaxFeeSats = 50_000
    };

    static async Task<string?> RefusalFor(PSBT psbt, bool guard = true)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        try
        {
            await signer.SignPsbtAsync(psbt.ToBase64(), Net, Policy(guard));
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    // TEST 0 — THE GATING TEST. Every other test here builds synthetic PSBTs from the signer's own
    // account keys, so they would all pass whichever of the three accounts were wired as "accepted".
    // This one determines the accepted account EMPIRICALLY by probing the guard, then ties it to
    // rgb-lib's own key generation. It fails if either the prefix-to-account labelling or the accept
    // decision is inverted — which is exactly the error the design carried for six review rounds.
    [Fact]
    public async Task Gate_AcceptedAccountIsTheOneRgbLibCallsVanilla()
    {
        var keysJson = RgbLibWallet.RestoreKeys("Regtest", TestMnemonic);
        using var doc = JsonDocument.Parse(keysJson);
        var root = doc.RootElement;
        var rgbVanillaXpub = root.GetProperty("account_xpub_vanilla").GetString();
        var rgbColoredXpub = root.GetProperty("account_xpub_colored").GetString();
        Assert.False(string.IsNullOrWhiteSpace(rgbVanillaXpub));
        Assert.False(string.IsNullOrWhiteSpace(rgbColoredXpub));

        var candidates = new[] { RgbVanillaAccount, RgbColoredAccount, Bip84Account };
        var accepted = new List<string>();
        foreach (var account in candidates)
        {
            var script = account == Bip84Account ? Segwit(account) : Taproot(account);
            var (_, psbt) = Build(script);
            if (await RefusalFor(psbt) == null)
                accepted.Add(account);
        }

        var acceptedAccount = Assert.Single(accepted);
        Assert.Equal(rgbVanillaXpub, Master().Derive(new KeyPath(acceptedAccount)).Neuter().ToString(Net));
        Assert.Equal(rgbColoredXpub, Master().Derive(new KeyPath(RgbColoredAccount)).Neuter().ToString(Net));
    }

    // The mainnet arm of the same tie-in. The regtest gate above probes the guard empirically, but it
    // can only speak for testnet coin types; mainnet is where real value sits, and an inverted mapping
    // there would be discovered by users rather than by tests. Ties mainnet to rgb-lib's own output
    // instead of restating the constants the guard already uses.
    [Fact]
    public void Gate_MainnetAccountsMatchWhatRgbLibReturns()
    {
        var keysJson = RgbLibWallet.RestoreKeys("Mainnet", TestMnemonic);
        using var doc = JsonDocument.Parse(keysJson);
        var rgbVanillaXpub = doc.RootElement.GetProperty("account_xpub_vanilla").GetString();
        var rgbColoredXpub = doc.RootElement.GetProperty("account_xpub_colored").GetString();

        using var signer = new MemoryWalletSigner(TestMnemonic, Network.Main);
        Assert.True(signer.TryClassifyAccount(new KeyPath("86'/0'/0'/0/0"), out var accepted));
        Assert.Equal(MemoryWalletSigner.PrevoutAccount.RgbLibVanilla, accepted);
        Assert.True(signer.TryClassifyAccount(new KeyPath("86'/827166'/0'/0/0"), out var refused));
        Assert.Equal(MemoryWalletSigner.PrevoutAccount.RgbLibColored, refused);

        Assert.Equal(rgbVanillaXpub,
            Master().Derive(new KeyPath("m/86'/0'/0'")).Neuter().ToString(Network.Main));
        Assert.Equal(rgbColoredXpub,
            Master().Derive(new KeyPath("m/86'/827166'/0'")).Neuter().ToString(Network.Main));
    }

    // TEST 1 — the burn the audit describes: a co-spent input carrying another contract's allocation.
    // Its claimed colored path is TRUTHFUL, so only the account check stops it.
    [Fact]
    public async Task ColoredKeychainInput_AlongsideVanilla_Refused()
    {
        var (_, psbt) = Build(Taproot(RgbVanillaAccount), Taproot(RgbColoredAccount));
        var msg = await RefusalFor(psbt);
        Assert.Contains("not rgb-lib's vanilla keychain", msg);
        Assert.Contains("RgbLibColored", msg);
    }

    [Fact]
    public async Task AllVanillaInputs_Sign()
    {
        var (_, psbt) = Build(Taproot(RgbVanillaAccount), Taproot(RgbVanillaAccount, 1));
        Assert.Null(await RefusalFor(psbt));
    }

    // TEST 3 — a forged claim: a colored script carrying a claimed VANILLA path. Classification is by
    // re-derivation, so the claim fails to verify against the script.
    [Fact]
    public async Task ForgedVanillaClaimOnColoredScript_Refused()
    {
        var (funding, psbt) = Build(Taproot(RgbColoredAccount));
        var forgedPath = new KeyPath($"{RgbVanillaAccount}/0/0".Replace("m/", ""));
        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            Pub(RgbColoredAccount, 0).GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(
                new HDFingerprint(Convert.FromHexString(new MemoryWalletSigner(TestMnemonic, Net).MasterFingerprint)),
                forgedPath)));
        var msg = await RefusalFor(psbt);
        Assert.Contains("does not match its prevout script", msg);
        Assert.NotNull(funding);
    }

    // TEST 4 — the catch-all: a witness-program prevout with no key path bearing this wallet's
    // fingerprint. Without the guard this dies on the pre-existing "was not signed" assertion, so the
    // distinct message is what makes the pin non-vacuous.
    [Fact]
    public async Task InputWithNoQualifyingKeyPath_Refused()
    {
        var foreign = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net).ScriptPubKey;
        var (_, psbt) = Build(Taproot(RgbVanillaAccount), foreign);
        Assert.Contains("no qualifying key path", await RefusalFor(psbt));
    }

    // TEST 5 — NonWitnessUtxo is accepted as a script source rather than refused, so an input that
    // supplies only the previous transaction still signs.
    [Fact]
    public async Task NonWitnessUtxoOnly_WithTruthfulVanillaClaim_Signs()
    {
        var (funding, psbt) = Build(Taproot(RgbVanillaAccount));
        psbt.Inputs[0].NonWitnessUtxo = funding;
        psbt.Inputs[0].WitnessUtxo = null;
        // The claim has to be supplied: PopulateInputKeyPaths skips an input with no WitnessUtxo, so
        // without it the guard would refuse at the no-qualifying-key-path step instead of reaching
        // the NonWitnessUtxo script resolution this test is about.
        AddTruthfulTaprootClaim(psbt.Inputs[0], RgbVanillaAccount, 0);
        Assert.Null(await RefusalFor(psbt));
    }

    static void AddTruthfulTaprootClaim(PSBTInput input, string account, uint idx)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var path = new KeyPath($"{account}/0/{idx}".Replace("m/", ""));
        input.HDTaprootKeyPaths.Add(
            Pub(account, idx).GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(
                new HDFingerprint(Convert.FromHexString(signer.MasterFingerprint)), path)));
    }

    // TEST 6 — a legacy prevout: the pre-segwit sighash does not commit to the input amount, so the
    // commitment argument that makes a forged prevout harmless would not hold.
    [Fact]
    public async Task NonWitnessProgramPrevout_Refused()
    {
        var legacy = Pub(RgbVanillaAccount, 0).GetAddress(ScriptPubKeyType.Legacy, Net).ScriptPubKey;
        var funding = Transaction.Create(Net);
        funding.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        funding.Outputs.Add(Money.Satoshis(100_000), legacy);

        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new OutPoint(funding, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(99_000), ChangeScript);
        var psbt = PSBT.FromTransaction(tx, Net);
        psbt.Inputs[0].NonWitnessUtxo = funding;

        Assert.Contains("not a witness program", await RefusalFor(psbt));
    }

    [Fact]
    public async Task ForeignFingerprintEntryOnly_Refused()
    {
        var foreignKey = new Key();
        var script = foreignKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Net).ScriptPubKey;
        var (_, psbt) = Build(Taproot(RgbVanillaAccount), script);
        psbt.Inputs[1].HDKeyPaths.Add(foreignKey.PubKey,
            new RootedKeyPath(new HDFingerprint(0xDEADBEEF), new KeyPath("86'/1'/0'/0/0")));
        Assert.Contains("no qualifying key path", await RefusalFor(psbt));
    }

    // TESTS 8 and 9 — the two prevout splits. Both are sanity-clean to NBitcoin and both would sign.
    [Fact]
    public async Task AmountSplit_Refused()
    {
        var (funding, psbt) = Build(Taproot(RgbVanillaAccount));
        psbt.Inputs[0].NonWitnessUtxo = funding;
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(5_000), Taproot(RgbVanillaAccount));
        Assert.Contains("conflicting utxo fields", await RefusalFor(psbt));
    }

    [Fact]
    public async Task ScriptSplit_Refused()
    {
        var legacy = Pub(RgbVanillaAccount, 0).GetAddress(ScriptPubKeyType.Legacy, Net).ScriptPubKey;
        var funding = Transaction.Create(Net);
        funding.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        funding.Outputs.Add(Money.Satoshis(100_000), legacy);

        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new OutPoint(funding, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(99_000), ChangeScript);
        var psbt = PSBT.FromTransaction(tx, Net);
        psbt.Inputs[0].NonWitnessUtxo = funding;
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(100_000), Segwit(RgbVanillaAccount));

        Assert.Contains("conflicting utxo fields", await RefusalFor(psbt));
    }

    // TEST 11 — the vestigial BIP84 account, which no rgb-lib descriptor ever references.
    [Fact]
    public async Task Bip84Input_Refused()
    {
        var (_, psbt) = Build(Taproot(RgbVanillaAccount), Segwit(Bip84Account));
        var msg = await RefusalFor(psbt);
        Assert.Contains("not rgb-lib's vanilla keychain", msg);
        Assert.Contains("UnusedBip84", msg);
    }

    // TEST 12 — the asset-send path must keep signing colored inputs; that is its purpose, and audit
    // finding B's fix protects it instead with an independent Stock scan.
    [Fact]
    public async Task ColoredInput_WithGuardOff_Signs()
    {
        var (_, psbt) = Build(Taproot(RgbColoredAccount));
        Assert.Null(await RefusalFor(psbt, guard: false));
    }

    [Fact]
    public async Task MainnetCoinTypes_Classified()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.Main);
        Assert.True(signer.TryClassifyAccount(new KeyPath("86'/0'/0'/0/0"), out var vanilla));
        Assert.Equal(MemoryWalletSigner.PrevoutAccount.RgbLibVanilla, vanilla);
        Assert.True(signer.TryClassifyAccount(new KeyPath("86'/827166'/0'/0/0"), out var colored));
        Assert.Equal(MemoryWalletSigner.PrevoutAccount.RgbLibColored, colored);
        Assert.True(signer.TryClassifyAccount(new KeyPath("84'/0'/0'/0/0"), out var bip84));
        Assert.Equal(MemoryWalletSigner.PrevoutAccount.UnusedBip84, bip84);
    }

    // TEST 14 — IsOwnScript's cache is keyed on the script alone and is populated by matches against
    // ANY account, so a classifier that consulted it would answer "owned" for a colored script.
    [Fact]
    public async Task ColoredScriptAlreadyInOwnScriptCache_StillRefused()
    {
        // Seed and probe the SAME signer instance: the cache is per-instance, so a fresh signer would
        // make this pass vacuously.
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        Assert.True(signer.IsOwnScript(Taproot(RgbColoredAccount), Net));

        var (_, psbt) = Build(Taproot(RgbColoredAccount));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), Net, Policy(guard: true)));
        Assert.Contains("not rgb-lib's vanilla keychain", ex.Message);
    }
}
