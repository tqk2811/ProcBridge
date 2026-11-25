using ProcBridge.HookIpc;

namespace ProcBridge.Tests.Lan;

/// <summary>Reads one reply body for <see cref="FakeHook.CallOkAsync{T}"/>.</summary>
internal delegate T HookBodyReader<T>(ref HookIpcReader reader);
