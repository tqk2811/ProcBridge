using ProcBridge.Broker;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Tests.Lan;

[TestClass]
public sealed class HookStatusMappingTests
{
    [TestMethod]
    public void PrivateStatuses_BecomeStandardNtStatus()
    {
        // STATUS_UNEXPECTED_NETWORK_ERROR and STATUS_INVALID_NETWORK_RESPONSE from ntstatus.h.
        Assert.AreEqual(0xC00000C4u, (uint)HookRequestHandler.ToHookStatus(NtStatus.FileVersionChanged));
        Assert.AreEqual(0xC00000C4u, (uint)HookRequestHandler.ToHookStatus(NtStatus.FrameTooLarge));
        Assert.AreEqual(0xC00000C3u, (uint)HookRequestHandler.ToHookStatus(NtStatus.ProtocolError));
    }

    [TestMethod]
    public void StandardStatuses_PassThrough()
    {
        foreach (NtStatus status in Enum.GetValues<NtStatus>())
        {
            if ((uint)status >= 0xE0000000u)
                continue;
            Assert.AreEqual(status, HookRequestHandler.ToHookStatus(status));
        }
    }

    [TestMethod]
    public void NoStatusLeavesTheCustomerRange()
    {
        foreach (NtStatus status in Enum.GetValues<NtStatus>())
            Assert.IsLessThan(0xE0000000u, (uint)HookRequestHandler.ToHookStatus(status), status.ToString());
    }
}
