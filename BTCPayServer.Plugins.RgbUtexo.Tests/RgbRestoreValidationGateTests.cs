using System.Linq;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RestoreSerial")]
public class RgbRestoreValidationGateTests
{
    const string ControllerFile = "Controllers/RGBController.cs";
    const string GateTypeFullName = "BTCPayServer.Plugins.RgbUtexo.Controllers.RgbRestoreValidationGate";

    [Fact]
    public async Task ASecondParentSideValidationArrivingWhileOneRunsIsRefusedInsteadOfBufferingASecondArchive()
    {
        var gate = new SemaphoreSlim(1, 1);
        var firstIsInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letTheFirstFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = RgbRestoreValidationGate.RunOneAtATimeOrRefuseAsync(gate, async () =>
        {
            firstIsInside.SetResult();
            await letTheFirstFinish.Task;
        });
        await firstIsInside.Task;

        var secondRan = false;
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbRestoreValidationGate.RunOneAtATimeOrRefuseAsync(gate, () =>
            {
                secondRan = true;
                return Task.CompletedTask;
            }));

        letTheFirstFinish.SetResult();
        await first;

        Assert.False(secondRan,
            "a second restore upload ran its archive validation while the first was still inside the gate, so "
            + "concurrent requests each copy their upload into a MemoryStream and decompress it in the BTCPay "
            + "parent process, which is the amplification the gate exists to stop");
        Assert.True(refusal.Message.Contains("Try again", StringComparison.Ordinal),
            $"the contention refusal reads [{refusal.Message}], which never tells the operator the refusal is "
            + "transient and clears itself; a merchant restoring the only copy of their RGB stock reads a "
            + "refusal with no retry instruction as the recovery path being closed");
    }

    [Fact]
    public async Task ThePermitIsReturnedAfterAFailedValidationSoAContendedRestoreIsNeverPermanentlyRefused()
    {
        var gate = new SemaphoreSlim(1, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbRestoreValidationGate.RunOneAtATimeOrRefuseAsync(
                gate, () => throw new InvalidOperationException("Backup file too small")));

        var admittedAfterTheFailure = false;
        var refusedAfterTheFailure = await Record.ExceptionAsync(
            () => RgbRestoreValidationGate.RunOneAtATimeOrRefuseAsync(gate, () =>
            {
                admittedAfterTheFailure = true;
                return Task.CompletedTask;
            }));

        Assert.True(admittedAfterTheFailure,
            "the gate kept its permit after a validation threw and answered the next restore with "
            + $"[{refusedAfterTheFailure?.Message ?? "nothing at all"}], so one malformed upload would refuse "
            + "every later restore for the life of the process and strand the assets of any merchant "
            + "recovering from backup");
    }

    [Fact]
    public async Task TheProcessWideGateAdmitsRestoresOneAfterAnotherSoTheRefusalIsTransient()
    {
        var admitted = 0;
        Exception? refusal = null;

        for (var attempt = 0; attempt < 3 && refusal == null; attempt++)
            refusal = await Record.ExceptionAsync(
                () => RgbRestoreValidationGate.RunOneAtATimeOrRefuseAsync(() =>
                {
                    admitted++;
                    return Task.CompletedTask;
                }));

        Assert.True(admitted == 3,
            $"the process-wide gate admitted {admitted} of 3 sequential restores and then answered "
            + $"[{refusal?.Message ?? "nothing at all"}], so its refusal is permanent rather than transient "
            + "and a merchant cannot recover by retrying");
    }

    [Fact]
    public void TheRestoreActionRunsItsParentSideArchiveValidationInsideTheProcessWideGate()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ControllerFile);
        var model = plugin.Model(tree);
        var action = RoslynPins.Method(tree, "RGBController", "RestoreFromBackup");

        var validationCalls = RoslynPins.BodyOf(action).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
                && method.Name == "ValidateBackupFileHeader")
            .ToList();

        Assert.True(validationCalls.Count == 1,
            $"expected exactly one ValidateBackupFileHeader call in RestoreFromBackup, found {validationCalls.Count}");

        var gated = validationCalls[0].Ancestors()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => model.GetSymbolInfo(invocation).Symbol as IMethodSymbol)
            .Any(method => method != null
                && method.Name == "RunOneAtATimeOrRefuseAsync"
                && method.ContainingType.ToDisplayString() == GateTypeFullName);

        Assert.True(gated,
            "RestoreFromBackup calls ValidateBackupFileHeader outside RgbRestoreValidationGate, so nothing bounds "
            + "how many restore uploads copy themselves into memory and decompress concurrently in the BTCPay "
            + "parent process; the single-flight gate in RGBWalletService.RestoreFromBackupAsync is taken only "
            + "after this validation has already run");
    }
}
