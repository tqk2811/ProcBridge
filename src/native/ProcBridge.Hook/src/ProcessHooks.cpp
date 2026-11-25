// Process creation hooks (plan 5.2): every child of a hooked process is hooked too.
//
// NtCreateUserProcess adds the "create suspended" flag and drops the breakaway-from-job flag (the
// child stays in the tree's Job Object). The child is not injected there: kernelbase registers the new
// process with csrss only after NtCreateUserProcess returns, and a remote thread run before that would
// initialize the child's loader (and kernel32) too early. kernelbase creates every process suspended and
// resumes the initial thread with NtResumeThread once the registration is done (or leaves that to the
// caller of CREATE_SUSPENDED). So the child is recorded as pending, and the first NtResumeThread on its
// initial thread (or NtResumeProcess on it) injects ProcBridge.Hook and runs the initializer first.
// A direct NtCreateUserProcess caller that did not ask for a suspended thread is injected and resumed
// right away (its thread would have run before any registration anyway).
#include "ProcessHooks.h"
#include "HookGuard.h"
#include <MinHook.h>
#include <atomic>
#include <vector>

namespace ProcBridge::Hook
{
    namespace
    {
        using NtCreateUserProcessFn = NTSTATUS(NTAPI*)(PHANDLE, PHANDLE, ACCESS_MASK, ACCESS_MASK, POBJECT_ATTRIBUTES,
            POBJECT_ATTRIBUTES, ULONG, ULONG, PVOID, PVOID, PVOID);
        using NtResumeThreadFn = NTSTATUS(NTAPI*)(HANDLE, PULONG);
        using NtResumeProcessFn = NTSTATUS(NTAPI*)(HANDLE);

        // NtCreateUserProcess ProcessFlags / ThreadFlags.
        constexpr ULONG ProcessCreateFlagsBreakaway = 0x00000001;
        constexpr ULONG ProcessCreateFlagsInheritHandles = 0x00000004;
        constexpr ULONG ThreadCreateFlagsCreateSuspended = 0x00000001;

        constexpr DWORD InjectTimeoutMs = 30000;
        // Exit code of a child the hook could not be injected into (it must not run unhooked).
        constexpr UINT InjectFailedExitCode = static_cast<UINT>(STATUS_DLL_INIT_FAILED);

        NtCreateUserProcessFn g_ntCreateUserProcess = nullptr;
        NtResumeThreadFn g_ntResumeThread = nullptr;
        NtResumeProcessFn g_ntResumeProcess = nullptr;

        Inject::HookPaths g_hooks;
        std::wstring g_environment;
        const IHandleInheritance* g_inheritance = nullptr;

        /// A child created suspended that has not been injected yet. Owns Process.
        struct PendingChild
        {
            DWORD ProcessId;
            DWORD ThreadId;
            HANDLE Process;
            bool InheritHandles;
        };

        /// Children waiting for their first resume. Lock-free when empty (the common case for
        /// NtResumeThread).
        class PendingChildren final
        {
        public:
            void Add(const PendingChild& child)
            {
                AcquireSRWLockExclusive(&_lock);
                // Children that were terminated before their first resume are dropped here.
                for (size_t i = 0; i < _children.size();)
                {
                    if (WaitForSingleObject(_children[i].Process, 0) == WAIT_OBJECT_0)
                    {
                        CloseHandle(_children[i].Process);
                        _children[i] = _children.back();
                        _children.pop_back();
                    }
                    else
                    {
                        ++i;
                    }
                }
                _children.push_back(child);
                _count.store(_children.size(), std::memory_order_release);
                ReleaseSRWLockExclusive(&_lock);
            }

            bool IsEmpty() const noexcept { return _count.load(std::memory_order_acquire) == 0; }

            /// Removes the children matching threadId (when not 0) or processId (when not 0).
            std::vector<PendingChild> Take(DWORD threadId, DWORD processId)
            {
                std::vector<PendingChild> taken;
                AcquireSRWLockExclusive(&_lock);
                for (size_t i = 0; i < _children.size();)
                {
                    const PendingChild& child = _children[i];
                    if ((threadId != 0 && child.ThreadId == threadId) || (processId != 0 && child.ProcessId == processId))
                    {
                        taken.push_back(child);
                        _children[i] = _children.back();
                        _children.pop_back();
                    }
                    else
                    {
                        ++i;
                    }
                }
                _count.store(_children.size(), std::memory_order_release);
                ReleaseSRWLockExclusive(&_lock);
                return taken;
            }

        private:
            SRWLOCK _lock = SRWLOCK_INIT;
            std::vector<PendingChild> _children;
            std::atomic<size_t> _count{ 0 };
        };

        PendingChildren g_pending;

        /// Injects the hook into a suspended child and closes child.Process. A child that cannot be
        /// hooked is terminated: it would otherwise run with the real file system only.
        void InjectChild(const PendingChild& child) noexcept
        {
            DWORD error = ERROR_SUCCESS;
            try
            {
                std::vector<Inject::InheritedHandle> handles;
                if (child.InheritHandles)
                {
                    g_inheritance->ExportForChild(handles);
                }
                const std::vector<BYTE> block = Inject::BuildInitBlock(g_environment, handles);
                error = Inject::InjectHook(child.Process, g_hooks, block.data(), block.size(), InjectTimeoutMs);
            }
            catch (...)
            {
                error = ERROR_NOT_ENOUGH_MEMORY;
            }
            if (error != ERROR_SUCCESS)
            {
                OutputDebugStringW(L"ProcBridge.Hook: cannot inject a child process, terminating it\n");
                TerminateProcess(child.Process, InjectFailedExitCode);
            }
            CloseHandle(child.Process);
        }

        void InjectPending(DWORD threadId, DWORD processId) noexcept
        {
            std::vector<PendingChild> children;
            try
            {
                children = g_pending.Take(threadId, processId);
            }
            catch (...)
            {
                return;
            }
            for (const PendingChild& child : children)
            {
                InjectChild(child);
            }
        }

        bool ReadHandle(PHANDLE pointer, HANDLE& value) noexcept
        {
            __try
            {
                value = *pointer;
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        NTSTATUS NTAPI HookNtCreateUserProcess(PHANDLE processHandle, PHANDLE threadHandle, ACCESS_MASK processDesiredAccess,
            ACCESS_MASK threadDesiredAccess, POBJECT_ATTRIBUTES processObjectAttributes, POBJECT_ATTRIBUTES threadObjectAttributes,
            ULONG processFlags, ULONG threadFlags, PVOID processParameters, PVOID createInfo, PVOID attributeList)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                // Processes the hook starts itself (the cross-architecture helper) stay unhooked.
                return g_ntCreateUserProcess(processHandle, threadHandle, processDesiredAccess, threadDesiredAccess,
                    processObjectAttributes, threadObjectAttributes, processFlags, threadFlags, processParameters, createInfo,
                    attributeList);
            }
            const bool callerSuspended = (threadFlags & ThreadCreateFlagsCreateSuspended) != 0;
            const NTSTATUS status = g_ntCreateUserProcess(processHandle, threadHandle, processDesiredAccess, threadDesiredAccess,
                processObjectAttributes, threadObjectAttributes, processFlags & ~ProcessCreateFlagsBreakaway,
                threadFlags | ThreadCreateFlagsCreateSuspended, processParameters, createInfo, attributeList);
            if (!NT_SUCCESS(status))
            {
                return status;
            }
            HANDLE process = nullptr;
            HANDLE thread = nullptr;
            if (!ReadHandle(processHandle, process) || !ReadHandle(threadHandle, thread))
            {
                return status;
            }
            PendingChild child{ GetProcessId(process), GetThreadId(thread), nullptr,
                (processFlags & ProcessCreateFlagsInheritHandles) != 0 };
            if (!DuplicateHandle(GetCurrentProcess(), process, GetCurrentProcess(), &child.Process, 0, FALSE, DUPLICATE_SAME_ACCESS))
            {
                OutputDebugStringW(L"ProcBridge.Hook: cannot keep a child process handle, terminating it\n");
                TerminateProcess(process, InjectFailedExitCode);
                return status;
            }
            if (!callerSuspended)
            {
                InjectChild(child);
                g_ntResumeThread(thread, nullptr);
                return status;
            }
            try
            {
                g_pending.Add(child);
            }
            catch (...)
            {
                InjectChild(child);
            }
            return status;
        }

        NTSTATUS NTAPI HookNtResumeThread(HANDLE threadHandle, PULONG previousSuspendCount)
        {
            if (g_pending.IsEmpty())
            {
                return g_ntResumeThread(threadHandle, previousSuspendCount);
            }
            ReentryGuard guard;
            if (guard.IsOutermost())
            {
                const DWORD threadId = GetThreadId(threadHandle);
                if (threadId != 0)
                {
                    InjectPending(threadId, 0);
                }
            }
            return g_ntResumeThread(threadHandle, previousSuspendCount);
        }

        NTSTATUS NTAPI HookNtResumeProcess(HANDLE processHandle)
        {
            if (g_pending.IsEmpty())
            {
                return g_ntResumeProcess(processHandle);
            }
            ReentryGuard guard;
            if (guard.IsOutermost())
            {
                const DWORD processId = GetProcessId(processHandle);
                if (processId != 0)
                {
                    InjectPending(0, processId);
                }
            }
            return g_ntResumeProcess(processHandle);
        }

        struct HookEntry
        {
            const char* Name;
            void* Detour;
            void** Original;
        };
    }

    DWORD InstallProcessHooks(const Inject::HookPaths& hooks, const std::wstring& environment,
        const IHandleInheritance& inheritance) noexcept
    {
        try
        {
            g_hooks = hooks;
            g_environment = environment;
        }
        catch (...)
        {
            return ERROR_NOT_ENOUGH_MEMORY;
        }
        g_inheritance = &inheritance;
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll == nullptr)
        {
            return GetLastError();
        }
        const HookEntry entries[] = {
            { "NtCreateUserProcess", reinterpret_cast<void*>(&HookNtCreateUserProcess), reinterpret_cast<void**>(&g_ntCreateUserProcess) },
            { "NtResumeThread", reinterpret_cast<void*>(&HookNtResumeThread), reinterpret_cast<void**>(&g_ntResumeThread) },
            { "NtResumeProcess", reinterpret_cast<void*>(&HookNtResumeProcess), reinterpret_cast<void**>(&g_ntResumeProcess) },
        };
        for (const HookEntry& entry : entries)
        {
            void* target = reinterpret_cast<void*>(GetProcAddress(ntdll, entry.Name));
            if (target == nullptr)
            {
                return ERROR_PROC_NOT_FOUND;
            }
            if (MH_CreateHook(target, entry.Detour, entry.Original) != MH_OK)
            {
                return ERROR_GEN_FAILURE;
            }
        }
        return ERROR_SUCCESS;
    }
}
