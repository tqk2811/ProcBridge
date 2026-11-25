#pragma once
// Per-thread re-entry guard shared by every hook (plan 4.6).

namespace ProcBridge::Hook
{
    inline thread_local unsigned t_hookDepth = 0;

    /// A hook that ends up calling a hooked function again on the same thread goes straight to the
    /// original: only the outermost hook on a thread does any work.
    class ReentryGuard final
    {
    public:
        ReentryGuard() noexcept : _outermost(t_hookDepth == 0) { ++t_hookDepth; }
        ~ReentryGuard() { --t_hookDepth; }
        ReentryGuard(const ReentryGuard&) = delete;
        ReentryGuard& operator=(const ReentryGuard&) = delete;

        bool IsOutermost() const noexcept { return _outermost; }

    private:
        bool _outermost;
    };
}
