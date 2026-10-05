// GPL-3.0-or-later. Windows cross-build compatibility for the rule-only adapter.
// No threads are created by PaddiRules. Upstream's transitive search headers
// require a NativeThread declaration; use its std::thread alternative on Zig,
// whose MinGW runtime provides C++ threads but does not ship pthread.h.
#ifndef THREAD_NATIVE_H_INCLUDED
#define THREAD_NATIVE_H_INCLUDED
#include <thread>
#include <utility>
namespace Stockfish {
struct NativeThreadOptions {
    bool largeStack{};
    NativeThreadOptions& setLargeStack(bool value) { largeStack = value; return *this; }
};
using NativeThread = std::thread;
template<class Function, class... Args>
NativeThread create_native_thread(NativeThreadOptions, Function&& fun, Args&&... args) {
    return NativeThread(std::forward<Function>(fun), std::forward<Args>(args)...);
}
}
#endif
