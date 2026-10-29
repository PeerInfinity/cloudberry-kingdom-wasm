/* Registers "__Native" as a P/Invoke module for the .NET wasm pinvoke table.
 * FNA binds emscripten_set_main_loop / emscripten_cancel_main_loop with
 * [DllImport("__Native")]; without a native file of this name the generator
 * leaves them out and the call fails at runtime. The symbols themselves come
 * from Emscripten's libc/html5 at link time. */
#include <emscripten/emscripten.h>
