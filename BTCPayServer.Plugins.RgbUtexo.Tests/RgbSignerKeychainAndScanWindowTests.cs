using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSignerKeychainAndScanWindowTests
{
    const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    static readonly Network Net = Network.RegTest;

    static Script TaprootScriptAt(string path)
    {
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath(path));
        return key.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
    }

    [Fact]
    public void ScriptFarAboveTheFastWindow_IsRefusedOnTheFastPathButFoundWhenTheCallerAssertsItIsOurs()
    {
        var script = TaprootScriptAt("m/86'/1'/0'/0/2500");

        using var fastPathSigner = new MemoryWalletSigner(TestMnemonic, Net);
        Assert.False(fastPathSigner.IsOwnScript(script, Net),
            "index 2500 is inside the fast scan window, so this test no longer exercises the escalation; "
            + "raise the index above MinScanBaseline + GapLimitScanBuffer");

        using var assertedSigner = new MemoryWalletSigner(TestMnemonic, Net);
        Assert.True(assertedSigner.IsOwnScript(script, Net, scriptIsAssertedToBeOurs: true),
            "a change address the plugin just obtained from rgb-lib was not recognised as ours because its "
            + "keychain index sits above the fast scan window. The window resets every process and SendBtc's "
            + "PSBT carries no derivations to calibrate it from, so without the escalation a long-lived "
            + "wallet's BTC sends become permanently unsignable and the failure reads as key corruption");
    }

    [Fact]
    public void ScriptAboveAnyFixedRescanBoundary_IsStillFound_SoTheModelHasOneDeclaredIndexBoundNotTwo()
    {
        var script = TaprootScriptAt("m/84'/1'/0'/0/11000");

        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        Assert.True(signer.IsOwnScript(script, Net, scriptIsAssertedToBeOurs: true),
            "an address at index 11000 was refused, so a fixed rescan ceiling below MaxReasonableIndex has "
            + "been reintroduced. IsAllowedAccountPath already declares MaxReasonableIndex as the model's "
            + "outer bound, so any lower scan ceiling is a second, undocumented boundary past which a funded "
            + "wallet's sends fail permanently — the exact shape this fix removed");
    }

    [Fact]
    public async Task EveryInputBindsEvenWhenTheirIndicesFormMoreClustersThanTheRescanBudget()
    {
        var master = new Mnemonic(TestMnemonic).DeriveExtKey();
        var vanillaAccount = master.Derive(new KeyPath("m/86'/1'/0'"));
        uint[] spreadIndices = [1500, 2000, 3000];

        var tx = Transaction.Create(Net);
        foreach (var _ in spreadIndices)
            tx.Inputs.Add(new TxIn(new OutPoint(RandomUtils.GetUInt256(), 0)));
        var destination = master.Derive(new KeyPath("m/86'/1'/0'/1/7"))
            .GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
        tx.Outputs.Add(new TxOut(Money.Satoshis(2_000L), destination.ScriptPubKey));

        var psbt = tx.CreatePSBT(Net);
        for (var i = 0; i < spreadIndices.Length; i++)
        {
            var script = vanillaAccount.Derive(new KeyPath($"1/{spreadIndices[i]}"))
                .GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
            psbt.Inputs[i].WitnessUtxo = new TxOut(Money.Satoshis(1_000L), script);
        }

        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var policy = new SigningPolicy
        {
            ExpectedDestination = destination.ToString(),
            ExpectedAmountSats = 2_000L,
            MaxFeeSats = 1_000L,
            MaxOutputCount = 1,
            RequireRgbVanillaKeychainInputs = true
        };

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), Net, policy);

        Assert.False(string.IsNullOrWhiteSpace(signed),
            "a legitimate multi-input send was refused because its inputs sat in more index clusters than "
            + "the exhaustive-rescan budget allowed. Every escalation here SUCCEEDS, so none of them is "
            + "evidence of a hostile PSBT — budgeting successful rescans strands a correct send behind an "
            + "error that tells the operator to re-sync a wallet that is not out of sync");
    }

    [Fact]
    public void InputBindingSweepsEachIndexOncePerPsbtRatherThanOncePerInput()
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, "Services", "MemoryWalletSigner.cs");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(m => m.Identifier.Text == "PopulateInputKeyPaths");
        Assert.True(method != null, "PopulateInputKeyPaths is absent; this bound cannot be checked");
        var body = method!.ToString();

        Assert.Contains("inputsAwaitingAPathByScript", body);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", body);
        Assert.True(
            body.Contains("MaxIndexIterationsPerPsbt", StringComparison.Ordinal)
            && body.Contains("indexIterationsRemaining-- <= 0", StringComparison.Ordinal),
            "the per-request iteration counter is gone. It is a BACKSTOP, not the live bound: while this "
            + "method sweeps exactly twice the structural bound is MaxReasonableIndex index steps per "
            + "request and the counter never fires. It earns its place by bounding the loop anyway if a "
            + "later change reintroduces per-input scanning or widens MaxReasonableIndex — the shape that "
            + "made a legitimate 11-input send permanently unsignable");

        var sweepCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(i => i.Expression.ToString() == "BindEverythingFoundInRange");
        Assert.True(sweepCalls == 2,
            $"the index sweep is invoked {sweepCalls} time(s); it must be exactly two — one over the fast "
            + "window and one over the remainder. Per-input scanning is what made the work budget able to "
            + "refuse a legitimate send permanently: with 11 UTXOs at indices 99990..100000 the per-input "
            + "shape needs about 1.1 million index steps, and because every input restarted its scan at "
            + "index 0 no retry ever made progress — a PERMANENT false reject, the forbidden category. "
            + "Sweeping once per PSBT bounds the whole request at MaxReasonableIndex index steps no matter "
            + "how many inputs it carries");

        Assert.True(
            body.IndexOf("indexIterationsRemaining-- <= 0", StringComparison.Ordinal)
                < body.IndexOf("foreach (var branch in branches)", StringComparison.Ordinal),
            "the work budget is charged after the per-branch sweep rather than before it");
    }

    [Fact]
    public void ColoredAccountKeychainOne_IsNotOwn_BecauseRgbLibsColoredDescriptorCoversKeychainZeroOnly()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);

        Assert.True(signer.IsOwnScript(TaprootScriptAt("m/86'/827167'/0'/0/0"), Net),
            "keychain 0 of the RGB colored account is exactly what rgb-lib's colored descriptor covers "
            + "(KEYCHAIN_RGB = 0), so refusing it would break every colored spend");
        Assert.False(signer.IsOwnScript(TaprootScriptAt("m/86'/827167'/0'/1/0"), Net),
            "keychain 1 of the RGB colored account is derivable from the seed but is outside rgb-lib's "
            + "colored descriptor, so an output placed there is invisible to rgb-lib and unspendable through "
            + "the plugin. Accepting it as wallet change lets a faulty or hostile PSBT producer strand the "
            + "selected balance while passing MaxUnknownOutputSats = 0");
    }

    [Fact]
    public void VanillaAccountKeychainOne_StaysOwn_BecauseRealRgbLibPsbtsUseIt()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);

        Assert.True(signer.IsOwnScript(TaprootScriptAt("m/86'/1'/0'/1/0"), Net),
            "keychain 1 of the rgb-lib vanilla account was refused. A real create_utxos_begin PSBT captured "
            + "from the pinned rgb-lib carries origin 86'/1'/0'/1/0 (see RgbLiveCreateUtxosPsbtFixtureTests), "
            + "so narrowing this account to keychain 0 refuses every Create-UTXOs — including the listener's "
            + "unattended path, which is the only way colorable UTXOs are made and therefore the only way "
            + "RGB can be received");
    }
}
