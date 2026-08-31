using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RgbLib;
using RgbRestoreHelper;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbNativeSendFreePathTests
{
    const string HelperFile = "RgbRestoreHelper/RgbNativeSend.cs";
    const string NativeMethodsTypeName = "RgbLib.NativeMethods";
    const string CResultStringTypeName = "RgbLib.CResultString";
    const string OfflineNativeCallThatAllocatesAStringNatively = "rgblib_get_address";
    const string RegtestAddressPrefix = "bcrt1";
    const int RepeatedAllocateAndFreeCycles = 64;

    const string WhyTheFreePathIsOnTheSuccessPath =
        "RgbNativeSend.ReadResult frees the rgb-lib payload in a finally block, so the free runs on the "
        + "SUCCESS path as well as the error path and an unreachable free replaces the returned text with "
        + "an exception. ReadResult is the only result reader for rgblib_send_begin and rgblib_send_end, "
        + "so a broken free there fails every RGB asset send rather than only failing sends.";

    [Fact]
    public void ReadResultReturnsTheNativelyAllocatedTextAndFreesItWithoutThrowing()
    {
        using var wallet = OfflineRegtestWallet.Create();
        var result = wallet.CallReturningCResultString(OfflineNativeCallThatAllocatesAStringNatively);
        Assert.Equal("Ok", OfflineRegtestWallet.StatusOf(result));
        Assert.NotEqual(IntPtr.Zero, OfflineRegtestWallet.PayloadPointerOf(result));

        string? text = null;
        var escaped = Record.Exception(() =>
            text = RgbNativeSend.ReadResult(result, OfflineNativeCallThatAllocatesAStringNatively));

        Assert.True(escaped is null,
            $"ReadResult threw {escaped?.GetType().Name} while reading a successful native result. "
            + WhyTheFreePathIsOnTheSuccessPath);
        Assert.True(text is not null && text.StartsWith(RegtestAddressPrefix, StringComparison.Ordinal),
            $"ReadResult must return the text rgb-lib allocated for "
            + $"{OfflineNativeCallThatAllocatesAStringNatively}; it returned "
            + $"{(text is null ? "null" : $"{text.Length} character(s) not starting with '{RegtestAddressPrefix}'")}. "
            + WhyTheFreePathIsOnTheSuccessPath);
        Assert.Equal(IntPtr.Zero, OfflineRegtestWallet.PayloadPointerOf(result));
    }

    [Fact]
    public void ReadResultSurvivesRepeatedRealAllocationAndFreeCycles()
    {
        using var wallet = OfflineRegtestWallet.Create();
        for (var cycle = 0; cycle < RepeatedAllocateAndFreeCycles; cycle++)
        {
            var result = wallet.CallReturningCResultString(OfflineNativeCallThatAllocatesAStringNatively);
            var text = RgbNativeSend.ReadResult(result, OfflineNativeCallThatAllocatesAStringNatively);
            Assert.True(text.StartsWith(RegtestAddressPrefix, StringComparison.Ordinal),
                $"cycle {cycle} read '{text}'. Each cycle allocates a string inside rgb-lib and hands the "
                + "pointer to the helper's free path, so a free that is not rgb-lib's own allocator "
                + "corrupts the native heap instead of failing an assertion. "
                + WhyTheFreePathIsOnTheSuccessPath);
        }
    }

    [Fact]
    public void EveryRgbLibMemberTheHelperNamesByStringResolvesInThePinnedAssembly()
    {
        var assembly = typeof(RgbLibWallet).Assembly;
        var nativeMethods = assembly.GetType(NativeMethodsTypeName);
        Assert.True(nativeMethods is not null,
            $"{NativeMethodsTypeName} is absent from the shipped RgbLib assembly "
            + $"({assembly.GetName().Version}); the helper resolves every native call through it");
        var resultType = assembly.GetType(CResultStringTypeName);
        Assert.True(resultType is not null,
            $"{CResultStringTypeName} is absent from the shipped RgbLib assembly");

        var root = HelperRoot();
        var literals = root.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.StringLiteralToken))
            .Where(t => t.Parent?.Ancestors().OfType<AttributeSyntax>().Any() != true)
            .Select(t => (string?)t.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var nativeNames = literals
            .Where(v => v.StartsWith("rgblib_", StringComparison.Ordinal)
                        || v.StartsWith("free_", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(nativeNames);
        foreach (var name in nativeNames)
            Assert.True(nativeMethods!.GetMethod(name) is not null,
                $"{HelperFile} names the native call '{name}' as a string, but "
                + $"{NativeMethodsTypeName} in the pinned RgbLib assembly has no such method, so the "
                + "reflective lookup that consumes it returns null at run time. The compiler cannot see "
                + "this and the helper only executes inside a spawned child process. Literals inside "
                + "attributes are exempt because a DllImport EntryPoint names a native export rather than "
                + $"a member of {NativeMethodsTypeName}; those are checked by "
                + $"{nameof(EveryDllImportTheHelperDeclaresResolvesToARealExportOfTheShippedNativeLibrary)}. "
                + WhyTheFreePathIsOnTheSuccessPath);

        foreach (var name in literals.Where(v => v.StartsWith("RgbLib.", StringComparison.Ordinal)))
            Assert.True(assembly.GetType(name) is not null,
                $"{HelperFile} names the type '{name}' as a string and it is absent from the pinned "
                + "RgbLib assembly");

        var fieldNames = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "GetField" && i.ArgumentList.Arguments.Count >= 1)
            .Select(i => i.ArgumentList.Arguments[0].Expression)
            .OfType<LiteralExpressionSyntax>()
            .Select(l => (string?)l.Token.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(fieldNames);
        foreach (var name in fieldNames)
        {
            var onWallet = typeof(RgbLibWallet).GetField(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var onResult = resultType!.GetField(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.True(onWallet is not null || onResult is not null,
                $"{HelperFile} reads the field '{name}' reflectively and it exists on neither "
                + $"RgbLibWallet nor {CResultStringTypeName} in the pinned RgbLib assembly");
        }
    }

    [Fact]
    public void EveryDllImportTheHelperDeclaresResolvesToARealExportOfTheShippedNativeLibrary()
    {
        var declarations = HelperRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Select(m => (Method: m, Import: m.AttributeLists.SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString() is "DllImport" or "DllImportAttribute")))
            .Where(x => x.Import is not null)
            .ToList();
        Assert.NotEmpty(declarations);

        foreach (var (method, import) in declarations)
        {
            var arguments = import!.ArgumentList?.Arguments ?? default;
            var library = arguments
                .Where(a => a.NameEquals is null)
                .Select(a => a.Expression)
                .OfType<LiteralExpressionSyntax>()
                .Select(l => (string?)l.Token.Value)
                .FirstOrDefault();
            Assert.True(!string.IsNullOrEmpty(library),
                $"{HelperFile} declares {method.Identifier.ValueText} with a DllImport whose library name "
                + "is not a string literal, so this pin cannot check that its entry point exists");

            var entryPoint = arguments
                .Where(a => a.NameEquals?.Name.Identifier.ValueText == "EntryPoint")
                .Select(a => a.Expression)
                .OfType<LiteralExpressionSyntax>()
                .Select(l => (string?)l.Token.Value)
                .FirstOrDefault() ?? method.Identifier.ValueText;

            var handle = NativeLibrary.Load(library!, typeof(RgbNativeSend).Assembly, null);
            Assert.True(NativeLibrary.TryGetExport(handle, entryPoint, out _),
                $"{HelperFile} declares an extern for '{entryPoint}' in '{library}', but the native "
                + $"library shipped for {RuntimeInformation.RuntimeIdentifier} exports no such symbol, so "
                + $"the first call to {method.Identifier.ValueText} throws EntryPointNotFoundException. "
                + WhyTheFreePathIsOnTheSuccessPath);
        }
    }

    const string WhyThePackageWrappersMustNotBeCalled =
        "The pinned RgbLib package imports free_wallet and free_invoice and no string deallocator at "
        + "all, so every wrapper that reads a CResultString strands the buffer rgb-lib allocated for "
        + "it. The helper must reach rgb-lib through RgbLib.NativeMethods and read any CResultString "
        + "with RgbNativeSend.ReadResult, which frees the payload in a finally block. Dispose is "
        + "exempt because it hands the wallet back to free_wallet instead of reading a result out of "
        + "it. A wrapper that returns a plain CResult cannot be routed through this helper's ReadResult "
        + "at all, because CResult.inner is a COpaqueStruct rather than a string pointer.";

    [Fact]
    public void TheHelperCallsNoRgbLibWalletWrapperBecauseNoneOfThemFreesWhatItReads()
    {
        var wrappers = typeof(RgbLibWallet)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                        | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.Name != nameof(IDisposable.Dispose))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(wrappers);

        var called = HelperRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax)
            .Select(MethodNameOf)
            .Where(wrappers.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(called.Count == 0,
            $"{HelperFile} invokes {string.Join(", ", called)}, which RgbLibWallet declares. This pin "
            + "matches on the method NAME alone, because the helper is outside the plugin compile set "
            + "and no semantic model resolves its receivers; a call to something else that happens to "
            + "share the name is refused too, and the answer is to rename that member rather than to "
            + $"relax this clause. {WhyThePackageWrappersMustNotBeCalled}");
    }

    [Fact]
    public void TheHelpersOnlineOptionsCarryEveryFieldRgbLibRequiresToDeserializeThem()
    {
        var options = OnlineOptionsObject();

        var fields = options.Initializers
            .Select(i => i.NameEquals?.Name.Identifier.ValueText
                         ?? (i.Expression as IdentifierNameSyntax)?.Identifier.ValueText
                         ?? string.Empty)
            .ToList();

        Assert.True(
            fields.OrderBy(f => f, StringComparer.Ordinal).SequenceEqual(
                new[] { "indexer_url", "skip_consistency_check", "vanilla_sync_lookback" },
                StringComparer.Ordinal),
            $"the helper sends OnlineOptions fields [{string.Join(", ", fields)}]. rgb-lib's OnlineOptions "
            + "declares indexer_url, skip_consistency_check and vanilla_sync_lookback with no serde "
            + "default on any of them, so a missing or misspelled field makes rgblib_go_online fail to "
            + "deserialize its argument and no wallet in the send helper can come online. Nothing "
            + "compiles against this shape and the helper runs only inside a spawned child process.");

        var lookbackMembers = options.Initializers
            .Where(i => (i.NameEquals?.Name.Identifier.ValueText
                         ?? (Unwrap(i.Expression) as IdentifierNameSyntax)?.Identifier.ValueText)
                        == "vanilla_sync_lookback")
            .ToList();
        Assert.True(lookbackMembers.Count == 1,
            $"the OnlineOptions payload declares {lookbackMembers.Count} member(s) named "
            + "'vanilla_sync_lookback'; it must declare exactly one");

        var lookback = Unwrap(lookbackMembers[0].Expression) as LiteralExpressionSyntax;
        Assert.True(lookback?.Token.Value is uint or int or long or ulong
                    && Convert.ToUInt64(lookback.Token.Value) == 100,
            $"vanilla_sync_lookback carries '{lookbackMembers[0].Expression}'; it must carry the "
            + "literal 100 the pinned RgbLib package sends, so routing around the package wrapper "
            + "leaves the vanilla-side sync window unchanged. This pin follows no assignments, so a "
            + "constant or local holding 100 is refused too; write the literal here.");

        var declaration = BringOnlineDeclaration();
        AssertFieldCarries(options, "indexer_url", ParameterOfType(declaration, "string"),
            "rgb-lib reads indexer_url as the URL to connect to; every other field name can be correct "
            + "while this one carries the wrong value, and rgb-lib rejects the payload or dials the "
            + "wrong endpoint from inside a spawned child process");
        AssertFieldCarries(options, "skip_consistency_check", ParameterOfType(declaration, "bool"),
            "skip_consistency_check decides whether rgb-lib validates the wallet against the indexer "
            + "before coming online; the call site chooses it and this field must carry that choice");
    }

    [Fact]
    public void TheHelperReadsTheGoOnlinePayloadThroughTheReaderThatFreesIt()
    {
        var method = BringOnlineDeclaration();

        var readCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == nameof(RgbNativeSend.ReadResult))
            .ToList();
        Assert.True(readCalls.Count == 1,
            $"BringOnlineFreeingTheNativeOnlinePayload makes {readCalls.Count} ReadResult call(s); it must "
            + "make exactly one. " + WhyThePackageWrappersMustNotBeCalled);

        var nativeCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "Invoke")
            .ToList();
        Assert.True(nativeCalls.Count == 1,
            $"BringOnlineFreeingTheNativeOnlinePayload calls rgb-lib {nativeCalls.Count} time(s); it must "
            + "call it exactly once. Every extra call allocates another CResultString payload, and this "
            + "test's single ReadResult can only free one of them. "
            + WhyThePackageWrappersMustNotBeCalled);

        var nativeResultLocals = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null && nativeCalls.Contains(Unwrap(v.Initializer.Value)))
            .Select(v => v.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var readArgument = readCalls[0].ArgumentList.Arguments.Count >= 1
            ? (Unwrap(readCalls[0].ArgumentList.Arguments[0].Expression) as IdentifierNameSyntax)
                ?.Identifier.ValueText
            : null;
        Assert.True(readArgument is not null && nativeResultLocals.Contains(readArgument),
            $"ReadResult is handed '{readArgument ?? readCalls[0].ArgumentList.ToString()}' rather than "
            + "the local the one rgb-lib call in this method initialized. This pin follows no "
            + "assignments, so an intermediate alias is refused even when it holds the same object; "
            + "pass the call's own local directly. Otherwise the payload that call allocated is not the "
            + "payload being freed. " + WhyThePackageWrappersMustNotBeCalled);

        var readLocals = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null && readCalls.Contains(Unwrap(v.Initializer.Value)))
            .Select(v => v.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        var onlineFieldLocals = LocalsResolvingWalletField(method, "_onlineJson");
        Assert.True(onlineFieldLocals.Count == 1,
            $"BringOnlineFreeingTheNativeOnlinePayload resolves RgbLibWallet._onlineJson into "
            + $"{onlineFieldLocals.Count} local(s); it must resolve it into exactly one so this pin can "
            + "follow what gets written there");

        var writes = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "SetValue"
                        && i.Expression is MemberAccessExpressionSyntax m
                        && m.Expression is IdentifierNameSyntax id
                        && onlineFieldLocals.Contains(id.Identifier.ValueText)
                        && i.ArgumentList.Arguments.Count == 2
                        && Unwrap(i.ArgumentList.Arguments[0].Expression) is IdentifierNameSyntax target
                        && target.Identifier.ValueText == WalletParameterOf(method))
            .ToList();
        Assert.True(writes.Count == 1,
            $"BringOnlineFreeingTheNativeOnlinePayload writes RgbLibWallet._onlineJson {writes.Count} "
            + "time(s); it must write it exactly once");

        var written = writes[0].ArgumentList.Arguments.Count == 2
            ? (Unwrap(writes[0].ArgumentList.Arguments[1].Expression) as IdentifierNameSyntax)
                ?.Identifier.ValueText
            : null;
        Assert.True(written is not null && readLocals.Contains(written),
            $"RgbLibWallet._onlineJson is written from '{written ?? writes[0].ArgumentList.ToString()}' "
            + "rather than a local ReadResult initialized. This pin follows no assignments, so an "
            + "intermediate alias is refused even when it holds the same text; write the reader's own "
            + "local directly. The field must carry the text ReadResult "
            + "returned: reading the rgblib_go_online payload any other way strands the string rgb-lib "
            + "allocated for it, and writing the field from anything else leaves every later native "
            + "call in the child process with online state rgb-lib did not produce. "
            + WhyThePackageWrappersMustNotBeCalled);
    }

    static bool ParameterTypeIs(TypeSyntax? type, string expected)
    {
        var written = type?.ToString().Split('.').Last();
        return written is not null
               && (written == expected
                   || (expected == "string" && written == "String")
                   || (expected == "bool" && written == "Boolean"));
    }

    static string WalletParameterOf(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters
            .Where(p => ParameterTypeIs(p.Type, nameof(RgbLibWallet)))
            .ToList();
        Assert.True(parameters.Count == 1,
            $"{method.Identifier.ValueText} takes {parameters.Count} {nameof(RgbLibWallet)} "
            + "parameter(s); it must take exactly one so this pin can check that the reflected reads "
            + "and writes target the wallet it was handed");
        return parameters[0].Identifier.ValueText;
    }

    static HashSet<string> LocalsResolvingWalletField(MethodDeclarationSyntax method, string fieldName) =>
        method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null
                        && v.Initializer.Value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                            .Any(i => MethodNameOf(i) == "GetField"
                                      && i.ArgumentList.Arguments.Count >= 1
                                      && Unwrap(i.ArgumentList.Arguments[0].Expression)
                                          is LiteralExpressionSyntax literal
                                      && (string?)literal.Token.Value == fieldName))
            .Select(v => v.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

    static void AssertFieldCarries(AnonymousObjectCreationExpressionSyntax options, string field,
        string expected, string why)
    {
        var members = options.Initializers
            .Where(i => (i.NameEquals?.Name.Identifier.ValueText
                         ?? (Unwrap(i.Expression) as IdentifierNameSyntax)?.Identifier.ValueText) == field)
            .ToList();
        Assert.True(members.Count == 1,
            $"the OnlineOptions payload declares {members.Count} member(s) named '{field}'; it must "
            + "declare exactly one");

        var carried = Unwrap(members[0].Expression);
        Assert.True(carried is IdentifierNameSyntax name && name.Identifier.ValueText == expected,
            $"OnlineOptions.{field} carries '{carried}'; it must carry '{expected}'. {why}.");
    }

    static string ParameterOfType(MethodDeclarationSyntax method, string type)
    {
        var parameters = method.ParameterList.Parameters
            .Where(p => ParameterTypeIs(p.Type, type))
            .ToList();
        Assert.True(parameters.Count == 1,
            $"{method.Identifier.ValueText} takes {parameters.Count} {type} parameter(s); it must take "
            + "exactly one so this pin can name the value its OnlineOptions field has to carry");
        return parameters[0].Identifier.ValueText;
    }

    static (string Name, List<ExpressionSyntax> Elements) NativeArgumentArray()
    {
        var method = BringOnlineDeclaration();
        var declarators = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null
                        && Unwrap(v.Initializer.Value)
                            is CollectionExpressionSyntax or ArrayCreationExpressionSyntax
                                or ImplicitArrayCreationExpressionSyntax)
            .ToList();
        Assert.True(declarators.Count == 1,
            $"BringOnlineFreeingTheNativeOnlinePayload declares {declarators.Count} array local(s); it "
            + "must declare exactly one, the argument array it hands to rgb-lib, so this pin can compare "
            + "it against the native signature");

        var initializer = Unwrap(declarators[0].Initializer!.Value);
        var elements = initializer switch
        {
            CollectionExpressionSyntax collection => collection.Elements
                .OfType<ExpressionElementSyntax>().Select(e => Unwrap(e.Expression)).ToList(),
            ArrayCreationExpressionSyntax array => array.Initializer?.Expressions
                .Select(Unwrap).ToList() ?? [],
            ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions
                .Select(Unwrap).ToList(),
            _ => []
        };
        return (declarators[0].Identifier.ValueText, elements);
    }

    static AnonymousObjectCreationExpressionSyntax OnlineOptionsObject()
    {
        var method = BringOnlineDeclaration();
        var serializeCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "Serialize")
            .ToList();
        Assert.True(serializeCalls.Count == 1 && serializeCalls[0].ArgumentList.Arguments.Count == 1,
            $"BringOnlineFreeingTheNativeOnlinePayload makes {serializeCalls.Count} Serialize call(s); "
            + "it must make exactly one, taking one argument, so this pin reads the object that actually "
            + "becomes the OnlineOptions payload rather than any anonymous object that happens to sit "
            + "nearby.");

        var serialized = Unwrap(serializeCalls[0].ArgumentList.Arguments[0].Expression);
        if (serialized is AnonymousObjectCreationExpressionSyntax inlined)
            return inlined;

        var name = (serialized as IdentifierNameSyntax)?.Identifier.ValueText;
        var bound = name is null
            ? new List<AnonymousObjectCreationExpressionSyntax>()
            : method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.Identifier.ValueText == name && v.Initializer != null)
                .Select(v => Unwrap(v.Initializer!.Value))
                .OfType<AnonymousObjectCreationExpressionSyntax>()
                .ToList();
        Assert.True(bound.Count == 1,
            $"the value serialized into the OnlineOptions payload is '{serialized}', which this pin "
            + "cannot resolve to exactly one anonymous object. rgb-lib deserializes that JSON into a "
            + "struct whose three fields carry no serde default, so serializing the wrong value fails at "
            + "run time inside a spawned child process and nothing compiles against it. Build the "
            + "payload from an anonymous object, inline or through a local this method declares.");
        return bound[0];
    }

    static MethodDeclarationSyntax BringOnlineDeclaration()
    {
        var methods = HelperRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "BringOnlineFreeingTheNativeOnlinePayload")
            .ToList();
        Assert.True(methods.Count == 1,
            $"{HelperFile} declares {methods.Count} method(s) named "
            + "BringOnlineFreeingTheNativeOnlinePayload; it must declare exactly one, the only place the "
            + "helper brings a wallet online without calling the package wrapper, so this pin knows "
            + "which body to read");
        return methods[0];
    }

    [Fact]
    public void TheSendPathBringsTheWalletOnlineThroughTheFreeingHelperRatherThanNotAtAll()
    {
        var entryPoints = HelperRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == nameof(RgbNativeSend.Invoke))
            .ToList();
        Assert.True(entryPoints.Count == 1,
            $"{HelperFile} declares {entryPoints.Count} method(s) named {nameof(RgbNativeSend.Invoke)}; "
            + "it must declare exactly one, the entry point the send child process calls, so this pin "
            + "knows which body has to reach the wallet online");

        var bringOnlineCalls = entryPoints[0].DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "BringOnlineFreeingTheNativeOnlinePayload")
            .ToList();
        Assert.True(bringOnlineCalls.Count == 1,
            $"{nameof(RgbNativeSend.Invoke)} calls BringOnlineFreeingTheNativeOnlinePayload "
            + $"{bringOnlineCalls.Count} time(s); it must call it exactly once. Declaring the helper "
            + "without calling it leaves RgbLibWallet._onlineJson unset, and every send in the child "
            + "process then fails with 'wallet is offline'. Nothing compiles against this call and the "
            + "helper runs only inside a spawned child process.");
    }

    [Fact]
    public void TheHelperWritesTheByRefWalletStructBackOntoTheWalletItCameFrom()
    {
        var method = BringOnlineDeclaration();
        var walletFieldLocals = LocalsResolvingWalletField(method, "_wallet");
        Assert.True(walletFieldLocals.Count == 1,
            $"BringOnlineFreeingTheNativeOnlinePayload resolves RgbLibWallet._wallet into "
            + $"{walletFieldLocals.Count} local(s); it must resolve it into exactly one");

        var argumentArrayLocals = new HashSet<string>(StringComparer.Ordinal)
        {
            NativeArgumentArray().Name
        };

        var writebacks = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "SetValue"
                        && i.Expression is MemberAccessExpressionSyntax m
                        && m.Expression is IdentifierNameSyntax id
                        && walletFieldLocals.Contains(id.Identifier.ValueText))
            .Where(i => i.ArgumentList.Arguments.Count == 2
                        && Unwrap(i.ArgumentList.Arguments[0].Expression) is IdentifierNameSyntax target
                        && target.Identifier.ValueText == WalletParameterOf(method)
                        && Unwrap(i.ArgumentList.Arguments[1].Expression)
                            is ElementAccessExpressionSyntax element
                        && element.Expression is IdentifierNameSyntax array
                        && argumentArrayLocals.Contains(array.Identifier.ValueText)
                        && element.ArgumentList.Arguments.Count == 1
                        && element.ArgumentList.Arguments[0].Expression
                            is LiteralExpressionSyntax { Token.Value: 0 })
            .ToList();
        Assert.True(writebacks.Count == 1,
            "BringOnlineFreeingTheNativeOnlinePayload must write element 0 of its native argument array "
            + $"back onto the _wallet field of the wallet it was handed exactly once; it does so "
            + $"{writebacks.Count} time(s). "
            + "rgb-lib takes the wallet by reference and reflection reflects the mutation only into the "
            + "argument array, so without the writeback every later native call in the child process "
            + "uses the pre-go-online wallet value.");

        var dispatches = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "Invoke")
            .ToList();
        Assert.True(dispatches.Count == 1,
            $"BringOnlineFreeingTheNativeOnlinePayload dispatches to rgb-lib {dispatches.Count} time(s); "
            + "it must dispatch exactly once so this pin can place the writeback relative to it");
        Assert.True(StatementOf(writebacks[0]).SpanStart > StatementOf(dispatches[0]).SpanStart,
            "the writeback onto RgbLibWallet._wallet runs BEFORE the rgb-lib dispatch. rgb-lib mutates "
            + "the wallet struct during the call and reflection surfaces that mutation only in the "
            + "argument array, so a writeback that runs first stores the pre-call value and every later "
            + "native call in the child process uses a wallet that never went online. Both statements "
            + "are present either way, so nothing but their order distinguishes the two outcomes.");
    }

    static SyntaxNode StatementOf(SyntaxNode node) =>
        node.Ancestors().OfType<StatementSyntax>().FirstOrDefault() ?? node;

    [Fact]
    public void TheHelperNeverReassignsALocalThesePinsFollowedToItsDeclaration()
    {
        var method = BringOnlineDeclaration();
        var locals = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(v => v.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        var reassignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax id && locals.Contains(id.Identifier.ValueText))
            .ToList();

        Assert.True(reassignments.Count == 0,
            "BringOnlineFreeingTheNativeOnlinePayload reassigns "
            + $"{string.Join(", ", reassignments.Select(a => a.ToString()))}. Every pin over this method "
            + "reads a value where it is DECLARED — the native result handed to ReadResult, the text "
            + "written onto _onlineJson, the argument array handed to rgb-lib. A later reassignment "
            + "leaves all of them green while a different value reaches rgb-lib or the freeing reader, "
            + "so the payload rgb-lib allocated is stranded or the wallet comes online with state "
            + "nothing produced. Introduce another local instead of reassigning one.");
    }

    [Fact]
    public void RgbLibGoOnlineTakesTheTwoArgumentsTheHelperSuppliesReflectively()
    {
        var nativeMethods = typeof(RgbLibWallet).Assembly.GetType(NativeMethodsTypeName);
        Assert.True(nativeMethods is not null, $"{NativeMethodsTypeName} is absent from the pinned assembly");

        var boundNatives = BringOnlineDeclaration().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "GetMethod" && i.ArgumentList.Arguments.Count >= 1)
            .Select(i => (Unwrap(i.ArgumentList.Arguments[0].Expression) as LiteralExpressionSyntax)
                ?.Token.Value as string)
            .ToList();
        Assert.True(boundNatives.Count == 1 && boundNatives[0] == "rgblib_go_online",
            "BringOnlineFreeingTheNativeOnlinePayload must resolve exactly the native "
            + $"'rgblib_go_online'; it resolves [{string.Join(", ", boundNatives.Select(n => n ?? "<non-literal>"))}]. "
            + "Every other clause here checks the shape of the call rather than its target, so a "
            + "different native would satisfy them all and then fail at run time inside a child process.");

        var goOnline = nativeMethods!.GetMethod(boundNatives[0]!);
        Assert.True(goOnline is not null,
            $"{NativeMethodsTypeName} in the pinned RgbLib assembly declares no {boundNatives[0]}");

        var parameters = goOnline!.GetParameters();
        Assert.True(parameters.Length == 2,
            $"rgblib_go_online takes {parameters.Length} parameter(s); the helper invokes it with a "
            + "two-element argument array, and a mismatch throws TargetParameterCountException on the "
            + "first send the child process attempts");
        Assert.True(parameters[0].ParameterType.IsByRef,
            "rgblib_go_online's first parameter must stay by-ref; the helper writes args[0] back onto "
            + "RgbLibWallet._wallet so the wallet struct rgb-lib mutated is the one later calls use");
        Assert.True(parameters[1].ParameterType == typeof(string),
            $"rgblib_go_online's second parameter is {parameters[1].ParameterType.Name}; the helper "
            + "passes the serialized OnlineOptions JSON as a string");

        var (arrayName, elements) = NativeArgumentArray();
        Assert.True(elements.Count == parameters.Length,
            $"the helper invokes rgblib_go_online with {elements.Count} argument(s) against a native "
            + $"signature of {parameters.Length}. Reflection rejects the mismatch on the first send the "
            + "child process attempts, and nothing compiles against this array.");

        var walletFieldLocals = LocalsResolvingWalletField(BringOnlineDeclaration(), "_wallet");
        Assert.True(elements.Count == parameters.Length
                    && elements[0] is InvocationExpressionSyntax read
                    && MethodNameOf(read) == "GetValue"
                    && read.Expression is MemberAccessExpressionSyntax access
                    && access.Expression is IdentifierNameSyntax field
                    && walletFieldLocals.Contains(field.Identifier.ValueText)
                    && read.ArgumentList.Arguments.Count == 1
                    && Unwrap(read.ArgumentList.Arguments[0].Expression) is IdentifierNameSyntax source
                    && source.Identifier.ValueText == WalletParameterOf(BringOnlineDeclaration()),
            $"argument 0 of rgblib_go_online is '{(elements.Count > 0 ? elements[0].ToString() : "<none>")}'; "
            + "it must read the _wallet field of the wallet this method was handed. rgb-lib takes that "
            + "struct by reference in parameter 0 and mutates it, so anything else either fails inside "
            + "the child process or brings a different wallet online while the caller's stays offline. "
            + "The argument count alone cannot see either outcome.");

        var optionsLocals = BringOnlineDeclaration().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null
                        && Unwrap(v.Initializer.Value) is InvocationExpressionSyntax build
                        && MethodNameOf(build) == "Serialize")
            .Select(v => v.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var argumentOneCarriesTheSerializedOptions =
            (elements[1] is IdentifierNameSyntax options
             && optionsLocals.Contains(options.Identifier.ValueText))
            || (elements[1] is InvocationExpressionSyntax inlined && MethodNameOf(inlined) == "Serialize");
        Assert.True(argumentOneCarriesTheSerializedOptions,
            $"argument 1 of rgblib_go_online is '{elements[1]}'; it must be the serialized OnlineOptions, "
            + "either as the local a Serialize call initialized or as that call inlined here. Swapping "
            + "the two arguments satisfies the count check and then fails inside the child process, "
            + "where nothing an operator can see explains it.");

        var dispatches = BringOnlineDeclaration().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "Invoke")
            .ToList();
        Assert.True(dispatches.Count == 1
                    && dispatches[0].ArgumentList.Arguments.Count == 2
                    && Unwrap(dispatches[0].ArgumentList.Arguments[1].Expression)
                        is IdentifierNameSyntax passed
                    && passed.Identifier.ValueText == arrayName,
            $"the one rgb-lib dispatch must pass '{arrayName}', the argument array checked above; it "
            + $"passes '{(dispatches.Count == 1 ? dispatches[0].ArgumentList.ToString() : $"{dispatches.Count} dispatch(es)")}'. "
            + "Checking the array's shape proves nothing if a different expression reaches "
            + "MethodInfo.Invoke. This pin follows no assignments, so an alias of the same array is "
            + "refused too; pass the declared local directly.");
    }

    static string MethodNameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => string.Empty
    };

    static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression switch
    {
        PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang
            => Unwrap(bang.Operand),
        ParenthesizedExpressionSyntax paren => Unwrap(paren.Expression),
        _ => expression
    };

    static SyntaxNode HelperRoot()
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, HelperFile);
        Assert.True(File.Exists(path), $"{HelperFile} is missing; it holds the live RGB send call site");
        return CSharpSyntaxTree
            .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path)
            .GetRoot();
    }

    sealed class OfflineRegtestWallet : IDisposable
    {
        const string BitcoinNetwork = "Regtest";
        const int MaxAllocationsPerUtxo = 5;

        readonly string _dataDir;
        readonly RgbLibWallet _wallet;
        readonly Type _nativeMethods;
        readonly Type _resultType;
        readonly FieldInfo _walletField;

        OfflineRegtestWallet(string dataDir, RgbLibWallet wallet, Type nativeMethods, Type resultType,
            FieldInfo walletField)
        {
            _dataDir = dataDir;
            _wallet = wallet;
            _nativeMethods = nativeMethods;
            _resultType = resultType;
            _walletField = walletField;
        }

        internal static OfflineRegtestWallet Create()
        {
            var assembly = typeof(RgbLibWallet).Assembly;
            var nativeMethods = assembly.GetType(NativeMethodsTypeName)
                ?? throw new InvalidOperationException(NativeMethodsTypeName);
            var resultType = assembly.GetType(CResultStringTypeName)
                ?? throw new InvalidOperationException(CResultStringTypeName);
            var walletField = typeof(RgbLibWallet).GetField("_wallet",
                                  BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? throw new InvalidOperationException("RgbLibWallet._wallet");

            using var keys = JsonDocument.Parse(RgbLibWallet.GenerateKeys(BitcoinNetwork));
            var dataDir = Path.Combine(Path.GetTempPath(), $"rgb-free-path-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDir);

            var walletConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["data_dir"] = dataDir,
                ["bitcoin_network"] = BitcoinNetwork,
                ["database_type"] = "Sqlite",
                ["max_allocations_per_utxo"] = MaxAllocationsPerUtxo,
                ["supported_schemas"] = new[] { "Nia", "Cfa" }
            });
            var keysConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["account_xpub_vanilla"] = keys.RootElement.GetProperty("account_xpub_vanilla").GetString(),
                ["account_xpub_colored"] = keys.RootElement.GetProperty("account_xpub_colored").GetString(),
                ["master_fingerprint"] = keys.RootElement.GetProperty("master_fingerprint").GetString(),
                ["vanilla_keychain"] = (int?)null,
                ["mnemonic"] = (string?)null
            });

            return new OfflineRegtestWallet(dataDir, new RgbLibWallet(walletConfig, keysConfig),
                nativeMethods, resultType, walletField);
        }

        internal object CallReturningCResultString(string nativeMethod)
        {
            var method = _nativeMethods.GetMethod(nativeMethod)
                ?? throw new MissingMethodException(NativeMethodsTypeName, nativeMethod);
            object?[] args = [_walletField.GetValue(_wallet)];
            var result = method.Invoke(null, args)
                ?? throw new InvalidOperationException($"{nativeMethod} returned null");
            _walletField.SetValue(_wallet, args[0]);
            Assert.Equal(_resultType, result.GetType());
            return result;
        }

        internal static string? StatusOf(object result) =>
            result.GetType().GetField("result")?.GetValue(result)?.ToString();

        internal static IntPtr PayloadPointerOf(object result) =>
            (IntPtr)(result.GetType().GetField("inner")?.GetValue(result) ?? IntPtr.Zero);

        public void Dispose()
        {
            _wallet.Dispose();
            try
            {
                Directory.Delete(_dataDir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
