using System.Globalization;
using System.Text.RegularExpressions;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;

namespace ProcBridge.Tests.HookIpc;

/// <summary>Keeps the hand written C++ header in sync with the C# definition.</summary>
[TestClass]
public sealed partial class HookIpcHeaderTests
{
    private static string ReadHeader()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "shared", "ProcBridge.HookIpc", "native", "ProcBridge", "HookIpc.h");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("HookIpc.h not found above " + AppContext.BaseDirectory);
    }

    private static long ParseNumber(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : long.Parse(text, CultureInfo.InvariantCulture);

    private static long Constant(string header, string name)
    {
        Match match = Regex.Match(header, $@"constexpr\s+\w+\s+{name}\s*=\s*(0x[0-9A-Fa-f]+|\d+)\s*;");
        Assert.IsTrue(match.Success, $"Constant {name} missing in HookIpc.h");
        return ParseNumber(match.Groups[1].Value);
    }

    private static Dictionary<string, long> EnumMembers(string header, string enumName)
    {
        Match block = Regex.Match(header, $@"enum\s+class\s+{enumName}\s*:\s*\w+\s*\{{(?<body>[^}}]*)\}}");
        Assert.IsTrue(block.Success, $"enum {enumName} missing in HookIpc.h");
        return EnumMemberRegex().Matches(block.Groups["body"].Value)
            .ToDictionary(x => x.Groups[1].Value, x => ParseNumber(x.Groups[2].Value));
    }

    [GeneratedRegex(@"(\w+)\s*=\s*(0x[0-9A-Fa-f]+|\d+)")]
    private static partial Regex EnumMemberRegex();

    [TestMethod]
    public void Constants_Match()
    {
        string header = ReadHeader();
        Assert.AreEqual((long)HookIpcProtocol.Version, Constant(header, "ProtocolVersion"));
        Assert.AreEqual((long)HookIpcProtocol.HeaderSize, Constant(header, "HeaderSize"));
        Assert.AreEqual((long)HookIpcProtocol.MaxMessageSize, Constant(header, "MaxMessageSize"));
        Assert.AreEqual((long)HookIpcProtocol.FileInfoSize, Constant(header, "FileInfoSize"));
        Assert.AreEqual((long)HookIpcProtocol.ListFlagHasMore, Constant(header, "ListFlagHasMore"));
        StringAssert.Contains(header, $@"L""\\\\.\\pipe\\{HookIpcProtocol.PipeNamePrefix}""");
    }

    [TestMethod]
    public void Enums_Match()
    {
        string header = ReadHeader();
        CollectionAssert.AreEquivalent(
            Enum.GetValues<HookIpcMessageType>().ToDictionary(x => x.ToString(), x => (long)x),
            EnumMembers(header, "MessageType"));
        CollectionAssert.AreEquivalent(
            Enum.GetValues<HookArchitecture>().ToDictionary(x => x.ToString(), x => (long)x),
            EnumMembers(header, "Architecture"));
    }
}
