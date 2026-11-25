// Process creation hooks (plan 5.2): every child of a hooked process is hooked too.
//
// NtCreateUserProcess adds the "create suspended" flag and drops the breakaway-from-job flag (the
// child stays in the tree's Job Object). The child is not injected there: kernelbase registers the new
// process with csrss only after NtCreateUserProcess returns, and a remote thread run before that would
// initialize the child's loader (and kernel32) too early. kernelbase creates every process suspended and
// resumes the initial thread with NtResumeThread once the registration is done (or leaves that to the
// caller of CREATE_SUSPENDED). So the child is recorded as pending, and the first NtResumeThread or
// NtAlertResumeThread on its initial thread (or NtResumeProcess on it) injects ProcBridge.Hook and runs
// the initializer first; a concurrent resume of the same child waits for that injection. The virtual
// handles the child inherits are captured at creation (CaptureInheritedHandles). A resume issued from
// another process is not seen (backlog).
// A direct NtCreateUserProcess caller that did not ask for a suspended thread is injected and resumed
// right away (its thread would have run before any registration anyway).
#include "ProcessHooks.h"
#include "HookGuard.h"
#include <MinHook.h>
#include <algorithm>
#include <atomic>
#include <cstddef>
#include <memory>
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

        // PS_ATTRIBUTE_LIST of NtCreateUserProcess (built by kernelbase from PROC_THREAD_ATTRIBUTE_*).
        // The attribute number is the low 16 bits; the high bits are thread/input/additive flags.
        constexpr ULONG_PTR PsAttributeNumberMask = 0x0000FFFF;
        constexpr ULONG_PTR PsAttributeThreadFlag = 0x00010000;
        constexpr ULONG_PTR PsAttributeParentProcess = 0; // PS_ATTRIBUTE_PARENT_PROCESS (0x60000)
        constexpr ULONG_PTR PsAttributeHandleList = 11;   // PS_ATTRIBUTE_HANDLE_LIST (0x2000B)

        struct PsAttribute
        {
            ULONG_PTR Attribute;
            SIZE_T Size;
            ULONG_PTR Value;
            PSIZE_T ReturnLength;
        };

        struct PsAttributeList
        {
            SIZE_T TotalLength;
            PsAttribute Attributes[1];
        };

        constexpr DWORD InjectTimeoutMs = 30000;
        // A resume that finds another thread injecting the same child waits for it: the injection runs
        // two remote threads, each bounded by InjectTimeoutMs (the helper adds a little more).
        constexpr DWORD InjectWaitMs = InjectTimeoutMs * 2 + 10000;
        // Exit code of a child the hook could not be injected into (it must not run unhooked).
        constexpr UINT InjectFailedExitCode = static_cast<UINT>(STATUS_DLL_INIT_FAILED);

        NtCreateUserProcessFn g_ntCreateUserProcess = nullptr;
        NtResumeThreadFn g_ntResumeThread = nullptr;
        NtResumeThreadFn g_ntAlertResumeThread = nullptr;
        NtResumeProcessFn g_ntResumeProcess = nullptr;

        Inject::HookPaths g_hooks;
        std::wstring g_environment;
        const IHandleInheritance* g_inheritance = nullptr;

        class ExclusiveLock final
        {
        public:
            explicit ExclusiveLock(SRWLOCK& lock) noexcept : _lock(lock) { AcquireSRWLockExclusive(&_lock); }
            ~ExclusiveLock() { ReleaseSRWLockExclusive(&_lock); }
            ExclusiveLock(const ExclusiveLock&) = delete;
            ExclusiveLock& operator=(const ExclusiveLock&) = delete;

        private:
            SRWLOCK& _lock;
        };

        /// A child created suspended that has not been injected yet. Owns Process and Done.
        struct PendingChild
        {
            DWORD ProcessId = 0;
            DWORD ThreadId = 0;
            HANDLE Process = nullptr;
            /// Built at creation: the virtual handles the child inherited at that moment (closing or
            /// reusing a handle before the first resume does not change what the child holds).
            std::vector<BYTE> InitBlock;
            /// Set (manual reset) once the injection is over; resumes that find the child being
            /// injected by another thread wait for it.
            HANDLE Done = nullptr;
            /// Wait on Process: drops the entry of a child that exits before its first resume.
            HANDLE ExitWait = nullptr;
            /// Guarded by the list lock: one thread injects, the others wait on Done.
            bool Injecting = false;

            PendingChild() = default;
            PendingChild(const PendingChild&) = delete;
            PendingChild& operator=(const PendingChild&) = delete;
            ~PendingChild()
            {
                if (Done != nullptr)
                {
                    CloseHandle(Done);
                }
                if (Process != nullptr)
                {
                    CloseHandle(Process);
                }
            }
        };

        using PendingChildPtr = std::shared_ptr<PendingChild>;

        void CALLBACK OnPendingChildExit(PVOID context, BOOLEAN timedOut);

        /// Children waiting for their first resume. Lock-free when empty (the common case for
        /// NtResumeThread).
        class PendingChildren final
        {
        public:
            /// Adds the child and watches its exit. Throws std::bad_alloc (the child is not added).
            void Add(const PendingChildPtr& child)
            {
                ExclusiveLock lock(_lock);
                _children.push_back(child);
                // Registered under the lock, so the callback (which takes the lock) sees ExitWait. Without
                // a wait (out of resources) a child that dies unresumed only stays listed.
                if (!RegisterWaitForSingleObject(&child->ExitWait, child->Process, OnPendingChildExit, child.get(), INFINITE,
                    WT_EXECUTEONLYONCE))
                {
                    child->ExitWait = nullptr;
                }
                _count.store(_children.size(), std::memory_order_release);
            }

            bool IsEmpty() const noexcept { return _count.load(std::memory_order_acquire) == 0; }

            /// Children matching threadId (when not 0) or processId (when not 0): those nobody injects yet
            /// go to toInject (marked as injecting), the others to toWait. Entries stay listed until
            /// Finish, so a concurrent resume of the same child finds them. Throws std::bad_alloc.
            void Begin(DWORD threadId, DWORD processId, std::vector<PendingChildPtr>& toInject, std::vector<PendingChildPtr>& toWait)
            {
                ExclusiveLock lock(_lock);
                for (const PendingChildPtr& child : _children)
                {
                    if ((threadId != 0 && child->ThreadId == threadId) || (processId != 0 && child->ProcessId == processId))
                    {
                        (child->Injecting ? toWait : toInject).push_back(child);
                        child->Injecting = true;
                    }
                }
            }

            /// Removes an injected child and wakes the resumes waiting for it.
            void Finish(const PendingChildPtr& child) noexcept
            {
                {
                    ExclusiveLock lock(_lock);
                    RemoveLocked(child.get());
                }
                SetEvent(child->Done);
                if (child->ExitWait != nullptr)
                {
                    // Blocks until a running exit callback returns (it leaves injecting entries alone).
                    UnregisterWaitEx(child->ExitWait, INVALID_HANDLE_VALUE);
                    child->ExitWait = nullptr;
                }
            }

            /// Exit callback: drops the entry unless a resume is injecting it (that resume cleans up).
            void OnExit(PendingChild* exited) noexcept
            {
                PendingChildPtr child;
                {
                    ExclusiveLock lock(_lock);
                    for (const PendingChildPtr& listed : _children)
                    {
                        if (listed.get() == exited)
                        {
                            if (!exited->Injecting)
                            {
                                child = listed;
                                RemoveLocked(exited);
                            }
                            break;
                        }
                    }
                }
                if (child != nullptr)
                {
                    // Non-blocking inside the callback; the entry is released when child goes out of scope.
                    UnregisterWait(child->ExitWait);
                    child->ExitWait = nullptr;
                }
            }

        private:
            void RemoveLocked(const PendingChild* removed) noexcept
            {
                for (size_t i = 0; i < _children.size(); ++i)
                {
                    if (_children[i].get() == removed)
                    {
                        _children[i] = std::move(_children.back());
                        _children.pop_back();
                        break;
                    }
                }
                _count.store(_children.size(), std::memory_order_release);
            }

            SRWLOCK _lock = SRWLOCK_INIT;
            std::vector<PendingChildPtr> _children;
            std::atomic<size_t> _count{ 0 };
        };

        PendingChildren g_pending;

        void CALLBACK OnPendingChildExit(PVOID context, BOOLEAN)
        {
            g_pending.OnExit(static_cast<PendingChild*>(context));
        }

        /// Injects the hook into a suspended child. A child that cannot be hooked is terminated: it would
        /// otherwise run with the real file system only.
        void InjectChild(const PendingChild& child) noexcept
        {
            const DWORD error = Inject::InjectHook(child.Process, g_hooks, child.InitBlock.data(), child.InitBlock.size(), InjectTimeoutMs);
            if (error != ERROR_SUCCESS)
            {
                OutputDebugStringW(L"ProcBridge.Hook: cannot inject a child process, terminating it\n");
                TerminateProcess(child.Process, InjectFailedExitCode);
            }
        }

        /// Injects the pending children a resume is about to start, or waits for the thread already
        /// injecting them, so no resume lets a child run before it is hooked.
        void InjectPending(DWORD threadId, DWORD processId) noexcept
        {
            std::vector<PendingChildPtr> toInject;
            std::vector<PendingChildPtr> toWait;
            try
            {
                g_pending.Begin(threadId, processId, toInject, toWait);
            }
            catch (...)
            {
                // Children marked before the failure are injected below; the others stay pending.
            }
            for (const PendingChildPtr& child : toInject)
            {
                InjectChild(*child);
                g_pending.Finish(child);
            }
            for (const PendingChildPtr& child : toWait)
            {
                WaitForSingleObject(child->Done, InjectWaitMs);
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

        struct InheritanceAttributes
        {
            bool HasParentProcess = false;
            HANDLE ParentProcess = nullptr;
            bool HasHandleList = false;
            const HANDLE* HandleList = nullptr;
            SIZE_T HandleListBytes = 0;
        };

        /// Handle inheritance attributes of the caller's PS_ATTRIBUTE_LIST, read under SEH (caller
        /// memory). False when the list cannot be read.
        bool ReadInheritanceAttributes(const void* attributeList, InheritanceAttributes& result) noexcept
        {
            if (attributeList == nullptr)
            {
                return true;
            }
            __try
            {
                const auto* list = static_cast<const PsAttributeList*>(attributeList);
                const SIZE_T total = list->TotalLength;
                if (total < offsetof(PsAttributeList, Attributes))
                {
                    return false;
                }
                const SIZE_T count = (total - offsetof(PsAttributeList, Attributes)) / sizeof(PsAttribute);
                for (SIZE_T i = 0; i < count; ++i)
                {
                    const PsAttribute& attribute = list->Attributes[i];
                    if ((attribute.Attribute & PsAttributeThreadFlag) != 0)
                    {
                        continue;
                    }
                    const ULONG_PTR number = attribute.Attribute & PsAttributeNumberMask;
                    if (number == PsAttributeParentProcess)
                    {
                        result.HasParentProcess = true;
                        result.ParentProcess = reinterpret_cast<HANDLE>(attribute.Value);
                    }
                    else if (number == PsAttributeHandleList)
                    {
                        result.HasHandleList = true;
                        result.HandleList = reinterpret_cast<const HANDLE*>(attribute.Value);
                        result.HandleListBytes = attribute.Size;
                    }
                }
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        bool CopyHandleList(const HANDLE* source, HANDLE* target, size_t count) noexcept
        {
            __try
            {
                memcpy(target, source, count * sizeof(HANDLE));
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        bool IsCurrentProcessHandle(HANDLE process) noexcept
        {
            return process == GetCurrentProcess() || (process != nullptr && GetProcessId(process) == GetCurrentProcessId());
        }

        /// The virtual handles the new child holds: the inheritable ones when it inherits handles from
        /// this process, narrowed to PROC_THREAD_ATTRIBUTE_HANDLE_LIST when the caller gave one; none when
        /// another process is its parent (it inherits that process's handles) or the attributes cannot
        /// be read. Throws std::bad_alloc.
        std::vector<Inject::InheritedHandle> CaptureInheritedHandles(ULONG processFlags, const void* attributeList)
        {
            std::vector<Inject::InheritedHandle> handles;
            InheritanceAttributes attributes;
            if ((processFlags & ProcessCreateFlagsInheritHandles) == 0 || !ReadInheritanceAttributes(attributeList, attributes)
                || (attributes.HasParentProcess && !IsCurrentProcessHandle(attributes.ParentProcess)))
            {
                return handles;
            }
            g_inheritance->ExportForChild(handles);
            if (!attributes.HasHandleList || handles.empty())
            {
                return handles;
            }
            std::vector<HANDLE> listed(attributes.HandleListBytes / sizeof(HANDLE));
            if (!CopyHandleList(attributes.HandleList, listed.data(), listed.size()))
            {
                handles.clear();
                return handles;
            }
            std::erase_if(handles, [&listed](const Inject::InheritedHandle& handle)
                {
                    const HANDLE value = reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(handle.Handle));
                    return std::find(listed.begin(), listed.end(), value) == listed.end();
                });
            return handles;
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
            if (!ReadHandle(processHandle, process))
            {
                // The kernel has just written it: unreadable now means the caller freed that memory, and
                // without the handle there is nothing to inject or terminate.
                return status;
            }
            if (!ReadHandle(threadHandle, thread))
            {
                OutputDebugStringW(L"ProcBridge.Hook: cannot read a child thread handle, terminating it\n");
                TerminateProcess(process, InjectFailedExitCode);
                return status;
            }
            PendingChildPtr child;
            try
            {
                child = std::make_shared<PendingChild>();
                child->ProcessId = GetProcessId(process);
                child->ThreadId = GetThreadId(thread);
                child->InitBlock = Inject::BuildInitBlock(g_environment, CaptureInheritedHandles(processFlags, attributeList));
            }
            catch (...)
            {
                OutputDebugStringW(L"ProcBridge.Hook: out of memory for a child process, terminating it\n");
                TerminateProcess(process, InjectFailedExitCode);
                return status;
            }
            child->Done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (child->Done == nullptr
                || !DuplicateHandle(GetCurrentProcess(), process, GetCurrentProcess(), &child->Process, 0, FALSE, DUPLICATE_SAME_ACCESS))
            {
                child->Process = nullptr;
                OutputDebugStringW(L"ProcBridge.Hook: cannot keep a child process handle, terminating it\n");
                TerminateProcess(process, InjectFailedExitCode);
                return status;
            }
            if (!callerSuspended)
            {
                InjectChild(*child);
                g_ntResumeThread(thread, nullptr);
                return status;
            }
            try
            {
                g_pending.Add(child);
            }
            catch (...)
            {
                InjectChild(*child);
            }
            return status;
        }

        /// Shared by the thread resume hooks (NtResumeThread, NtAlertResumeThread).
        NTSTATUS ResumeThreadHooked(NtResumeThreadFn original, HANDLE threadHandle, PULONG previousSuspendCount)
        {
            if (g_pending.IsEmpty())
            {
                return original(threadHandle, previousSuspendCount);
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
            return original(threadHandle, previousSuspendCount);
        }

        NTSTATUS NTAPI HookNtResumeThread(HANDLE threadHandle, PULONG previousSuspendCount)
        {
            return ResumeThreadHooked(g_ntResumeThread, threadHandle, previousSuspendCount);
        }

        NTSTATUS NTAPI HookNtAlertResumeThread(HANDLE threadHandle, PULONG previousSuspendCount)
        {
            return ResumeThreadHooked(g_ntAlertResumeThread, threadHandle, previousSuspendCount);
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
            { "NtAlertResumeThread", reinterpret_cast<void*>(&HookNtAlertResumeThread), reinterpret_cast<void**>(&g_ntAlertResumeThread) },
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
