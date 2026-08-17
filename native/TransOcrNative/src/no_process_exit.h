// Force-included into every TransOcrNative translation unit.
//
// The vendored PaddleOCR C++ inference pipeline calls exit(-1) on dozens of
// error paths. Inside the WinUI host that silently terminates the whole
// process (no exception, no WER report — observed as exit code -1). Redirect
// those calls to a C++ exception so trans_ocr.cpp can catch them and report
// a recoverable error to the app.
//
// <cstdlib> and <process.h> are included first so the real exit()
// declarations are already processed before the macro takes effect.
#pragma once

#include <cstdlib>
#include <process.h>
#include <stdexcept>
#include <string>

[[noreturn]] inline void TransOcrDisabledExit(int status) {
    throw std::runtime_error(
        "PaddleOCR pipeline attempted to terminate the process with exit(" +
        std::to_string(status) + ")");
}

#define exit(status) TransOcrDisabledExit(status)
