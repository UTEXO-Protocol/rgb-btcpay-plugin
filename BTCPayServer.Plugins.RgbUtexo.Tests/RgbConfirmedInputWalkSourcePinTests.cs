using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbConfirmedInputWalkSourcePinTests
{
    const string WalletServiceFile = "Services/RGBWalletService.cs";

    static MethodDeclarationSyntax MethodNamed(string name)
    {
        var matches = PluginCompilation.Shared.Tree(WalletServiceFile).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == name)
            .ToList();

        Assert.True(matches.Count == 1,
            $"expected exactly one {name} in {WalletServiceFile}, found {matches.Count}");
        return matches[0];
    }

    [Fact]
    public void TheSendPathEstablishesConfirmationBeforeItChoosesInputs()
    {
        var body = MethodNamed("SendBtcInternalAsync").ToString();
        var walkAt = body.IndexOf("WalkConfirmedCandidatesAsync", StringComparison.Ordinal);
        var chooseAt = body.IndexOf("ChooseOrRefuse", StringComparison.Ordinal);

        Assert.True(walkAt >= 0, "SendBtcInternalAsync must run the confirmation walk.");
        Assert.True(chooseAt >= 0, "SendBtcInternalAsync must select inputs through ChooseOrRefuse.");
        Assert.True(walkAt < chooseAt,
            "Confirmation must be established before inputs are chosen; choosing first and checking "
            + "afterwards would let an unconfirmed output reach the signer.");
    }

    [Fact]
    public void TheWalksConfirmationSourceIsTheRealChainLookup()
    {
        var body = MethodNamed("SendBtcInternalAsync").ToString();
        var walkAt = body.IndexOf("WalkConfirmedCandidatesAsync", StringComparison.Ordinal);
        var chooseAt = body.IndexOf("ChooseOrRefuse", StringComparison.Ordinal);
        var lookupAt = body.IndexOf("ConfirmationOfAsync", StringComparison.Ordinal);

        Assert.True(lookupAt > walkAt && lookupAt < chooseAt,
            "The confirmation callback handed to the walk must be ConfirmationOfAsync, which asks the "
            + "indexer. Order alone is not enough: substituting a constant such as "
            + "(_, _) => Task.FromResult<bool?>(true) would keep the walk and the decision in place "
            + "while accepting every unmined output, which is exactly the defect this change closes.");
    }

    static InvocationExpressionSyntax SoleCallTo(MethodDeclarationSyntax method, string name)
    {
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().EndsWith(name, StringComparison.Ordinal))
            .ToList();

        Assert.True(calls.Count == 1,
            $"expected exactly one {name} call in {method.Identifier.ValueText}, found {calls.Count}");
        return calls[0];
    }

    static string LocalAssignedFrom(MethodDeclarationSyntax method, string calleeName)
    {
        var declarators = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer is not null
                && v.Initializer.Value.ToString().Contains(calleeName, StringComparison.Ordinal))
            .ToList();

        Assert.True(declarators.Count == 1,
            $"expected exactly one local initialised from {calleeName}, found {declarators.Count}");
        return declarators[0].Identifier.ValueText;
    }

    [Fact]
    public void TheSelectorIsGivenOnlyTheOutputsTheWalkConfirmed()
    {
        var method = MethodNamed("SendBtcInternalAsync");
        var walkLocal = LocalAssignedFrom(method, "WalkConfirmedCandidatesAsync");
        var firstArgument = SoleCallTo(method, "ChooseOrRefuse").ArgumentList.Arguments[0].Expression;

        Assert.True(
            firstArgument is MemberAccessExpressionSyntax access
                && access.Expression.ToString() == walkLocal
                && access.Name.Identifier.ValueText == "Confirmed",
            "The candidates handed to ChooseOrRefuse must be the walk's confirmed subset, not the "
            + "unfiltered list the candidates were projected from. Passing the original list back "
            + $"leaves the walk running and every other pin green while the selector picks an "
            + $"unmined output and the wallet signs it. Found '{firstArgument}'.");
    }

    [Fact]
    public void TheTransactionIsBuiltFromTheInputsTheSelectorReturned()
    {
        var method = MethodNamed("SendBtcInternalAsync");
        var choiceLocal = LocalAssignedFrom(method, "ChooseOrRefuse");
        var selectedLocal = LocalAssignedFrom(method, $"{choiceLocal}.Inputs");

        var addsThoseInputs = method.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Any(loop => loop.Expression.ToString() == selectedLocal
                && loop.Statement.ToString().Contains("Inputs.Add", StringComparison.Ordinal));

        Assert.True(addsThoseInputs,
            $"The transaction's inputs must be added by iterating '{selectedLocal}', the list built "
            + "from the selector's result. Reading the selector's result into a local and then "
            + "building the transaction from some other list would establish confirmation and throw "
            + "it away, which is the defect this change exists to close.");

        var witnessLoopReadsThem = method.DescendantNodes().OfType<ForStatementSyntax>()
            .Any(loop => loop.Statement.ToString().Contains($"{selectedLocal}[", StringComparison.Ordinal)
                && loop.Statement.ToString().Contains("WitnessUtxo", StringComparison.Ordinal));

        Assert.True(witnessLoopReadsThem,
            $"Each input's WitnessUtxo must be taken from '{selectedLocal}' too. Signing values read "
            + "from a different list than the one that supplied the inputs would let the signer "
            + "approve a fee computed over outputs the transaction does not actually spend.");
    }

    [Fact]
    public void TheConfirmationCallbackAsksAboutTheCandidateTheWalkIsExamining()
    {
        var method = MethodNamed("SendBtcInternalAsync");
        var walkCall = SoleCallTo(method, "WalkConfirmedCandidatesAsync");

        var lambda = walkCall.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<ParenthesizedLambdaExpressionSyntax>()
            .Single();
        var outpointParameter = lambda.ParameterList.Parameters[0].Identifier.ValueText;

        var lookup = lambda.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression.ToString().EndsWith("ConfirmationOfAsync", StringComparison.Ordinal));

        Assert.Contains(lookup.ArgumentList.Arguments,
            a => a.Expression.ToString() == outpointParameter);

        Assert.True(
            lookup.ArgumentList.Arguments.All(a =>
                !a.Expression.ToString().Contains(".Outpoint", StringComparison.Ordinal)),
            "The confirmation lookup must ask about the outpoint the walk handed it, and nothing "
            + "else. Reaching past the parameter for some other output's outpoint compiles, keeps "
            + "every other pin green, and collapses every candidate onto one output's confirmation "
            + "state, so a single mined output would make the whole wallet look confirmed.");
    }

    [Fact]
    public void ParentTransactionsAreOnlyEverReadThroughTheFetchThatCanSupplyThem()
    {
        var method = MethodNamed("SendBtcInternalAsync");
        var cacheLocal = LocalAssignedFrom(method, "new Dictionary<string, Transaction>");

        var readDirectly = method.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .Count(access => access.Expression.ToString() == cacheLocal);

        Assert.True(readDirectly == 0,
            $"'{cacheLocal}' must only ever be read through ParentTransactionAsync, which fetches on "
            + $"a miss; found {readDirectly} direct lookups. A candidate whose confirmation was "
            + "answered from another output's cached rows never fetched its own parent, so indexing "
            + "the cache directly throws KeyNotFoundException at the operator on an ordinary multi "
            + "input send.");
    }

    [Fact]
    public void TheSigningLoopObtainsEachInputsParentRatherThanAssumingItIsPresent()
    {
        var method = MethodNamed("SendBtcInternalAsync");

        var fetchesWhereItSigns = method.DescendantNodes().OfType<ForStatementSyntax>()
            .Any(loop => loop.Statement.ToString().Contains("WitnessUtxo", StringComparison.Ordinal)
                && loop.Statement.ToString()
                    .Contains("ParentTransactionAsync", StringComparison.Ordinal));

        Assert.True(fetchesWhereItSigns,
            "The loop that assigns WitnessUtxo must obtain each parent through ParentTransactionAsync "
            + "so a parent that was never fetched during the confirmation walk is fetched here. "
            + "Reading it from anywhere that cannot supply a missing entry reintroduces the crash.");
    }

    [Fact]
    public void TheSendPathHoldsNoFeeArithmeticOfItsOwn()
    {
        var body = MethodNamed("SendBtcInternalAsync").ToString();

        Assert.DoesNotContain("EstimateTaprootFee", body);
        Assert.DoesNotContain("546", body);
    }

    [Fact]
    public void TheChangeAddressIsDerivedExactlyOnce()
    {
        var body = MethodNamed("SendBtcInternalAsync").ToString();
        var derivations = body.Split("GetAddressAsync").Length - 1;

        Assert.True(derivations == 1,
            $"GetAddressAsync must be called exactly once in SendBtcInternalAsync; found "
            + $"{derivations}. More than one call means a change address is being derived per "
            + "candidate rather than per send, which burns an address index on every examined UTXO "
            + "and risks the transaction being built against a different address than the signing "
            + "policy allows.");
    }
}
