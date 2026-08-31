using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbVerifyBindingTests
{
    const string ValidateJson = """
    {
      "contractId": "rgb:Cfn6bJvN-r_xEQET-1DslmTr-rCbxbgR-S0kAu2_-8XkSq~A",
      "chainNet": "bcrt",
      "witnessTxid": "a68be11f883a9423bd7a3ba729e2dfd417e0db054d8d8e2a7ddc371401fd79fc",
      "prevouts": ["a68be11f883a9423bd7a3ba729e2dfd417e0db054d8d8e2a7ddc371401fd79fc:0"],
      "legs": [
        { "assignmentType": 4000, "sealKind": "confidentialSeal", "sealBytes": "aabb", "witnessVout": null, "outpoint": null, "amount": 100 }
      ],
      "inputsAccounted": false,
      "inputs": [
        {
          "outpoint": "a1306969b0972548d686657e8c9f93ab9a7a9df2dba116fcae5586adf4ac81b6:1",
          "observed": [
            { "contractId": "rgb:Cfn6bJvN-r_xEQET-1DslmTr-rCbxbgR-S0kAu2_-8XkSq~A", "kind": "amount", "amount": 5000, "accounted": true, "reason": "accountedTransferInput" },
            { "contractId": "rgb:Q3BzNdGX-EbHQ65U-AN4Px9g-6tlkViw-Lzn9uLo-yRriVco", "kind": "amount", "amount": 42, "accounted": false, "reason": "foreignContract" },
            { "contractId": "rgb:jbWkxjFq-ZTzP50O-uLTADZi-RFUMLXk-aKzH2UI-t7K4RyI", "kind": "data", "amount": null, "accounted": false, "reason": "nonFungibleOnInput" }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void ExtendedValidateResult_DeserializesInputsAndAccountingFlag()
    {
        var r = JsonSerializer.Deserialize<RgbValidateResult>(ValidateJson);
        Assert.NotNull(r);
        Assert.False(r!.InputsAccounted);
        Assert.Single(r.Inputs);
        var input = r.Inputs[0];
        Assert.Equal("a1306969b0972548d686657e8c9f93ab9a7a9df2dba116fcae5586adf4ac81b6:1", input.Outpoint);
        Assert.Equal(3, input.Observed.Count);

        var accounted = input.Observed.Single(o => o.Accounted);
        Assert.Equal("amount", accounted.Kind);
        Assert.Equal(5000ul, accounted.Amount);
        Assert.Equal("accountedTransferInput", accounted.Reason);

        var foreign = input.Observed.Single(o => o.Reason == "foreignContract");
        Assert.False(foreign.Accounted);

        var nonFungible = input.Observed.Single(o => o.Reason == "nonFungibleOnInput");
        Assert.Equal("data", nonFungible.Kind);
        Assert.Null(nonFungible.Amount);
    }

    [Fact]
    public void MissingInputs_DefaultsToClosedShape()
    {
        var r = JsonSerializer.Deserialize<RgbValidateResult>(
            """{ "contractId": "rgb:x", "chainNet": "bcrt", "witnessTxid": "00", "prevouts": [], "legs": [] }""");
        Assert.NotNull(r);
        Assert.False(r!.InputsAccounted);
        Assert.Empty(r.Inputs);
    }

    [Fact]
    public void MissingV2SecurityFields_DefaultToRejectingShape()
    {
        var r = JsonSerializer.Deserialize<RgbValidateV2Result>(
            """{ "contractId": "rgb:x", "chainNet": "bcrt", "witnessTxid": "00" }""");
        Assert.NotNull(r);
        Assert.Equal(0, r!.ValidationVersion);
        Assert.False(r.InputsAccounted);
        Assert.False(r.CommitmentMatches);
        Assert.False(r.WitnessIdMatches);
        Assert.Empty(r.CommittedContractIds);
        Assert.Empty(r.VerifiedContractIds);
        Assert.Empty(r.VerifiedTransitionIds);
        Assert.Empty(r.CarryForwards);
        Assert.Equal(string.Empty, r.MainTransitionId);
    }

    // Proves the native library loads through the DllImportResolver and the CResultString
    // free discipline runs: a malformed invoice returns Err, surfaced as the typed exception.
    [Fact]
    public void NativeDecodeInvoice_Malformed_ThrowsThroughFreePath()
    {
        Assert.Throws<RgbIntentVerificationException>(
            () => RgbVerifyNative.DecodeInvoice("not-a-valid-rgb-invoice"));
    }
}
