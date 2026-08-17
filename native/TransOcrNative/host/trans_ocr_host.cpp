// Out-of-process OCR host. The vendored PaddleOCR pipeline can terminate its
// process on internal errors (exit(-1), abort, fail-fast), so inference runs
// here instead of inside the WinUI app. The app talks to this process with a
// small length-prefixed binary protocol:
//
//   request : [int32 width][int32 height][int32 stride][uint64 pixelBytes][pixels]
//   response: [int32 status][uint64 payloadBytes][payload]
//             payload = result JSON (status == TRANS_OCR_OK) or error UTF-8
//
// Requests are read from stdin; responses are written to a dedicated anonymous
// pipe whose inherited handle is passed as argv[1]. stdout is left to the
// vendored Paddle logging (fprintf(stdout, ...)), so a log flush can never
// corrupt a response frame.
//
// On startup the host creates the engine and reports readiness as one int32:
// 0 = ready. On init failure it prints the reason to stderr and exits 1.

#include "trans_ocr.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>
#include <vector>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace {

#ifdef _WIN32
using ResponseHandle = HANDLE;
#else
using ResponseHandle = int;
#endif

ResponseHandle g_response{};

bool read_exact(void* buffer, std::size_t bytes) {
    auto* target = static_cast<uint8_t*>(buffer);
    while (bytes > 0) {
        const std::size_t chunk = std::fread(target, 1, bytes, stdin);
        if (chunk == 0) {
            return false; // EOF or error: parent went away.
        }
        target += chunk;
        bytes -= chunk;
    }
    return true;
}

bool write_exact(const void* buffer, std::size_t bytes) {
    const auto* source = static_cast<const uint8_t*>(buffer);
    while (bytes > 0) {
#ifdef _WIN32
        DWORD chunk = 0;
        if (!WriteFile(g_response, source, static_cast<DWORD>(bytes), &chunk, nullptr) ||
            chunk == 0) {
            return false;
        }
#else
        const ssize_t chunk = ::write(g_response, source, bytes);
        if (chunk <= 0) {
            return false;
        }
#endif
        source += chunk;
        bytes -= chunk;
    }
    return true;
}

template <typename T>
bool read_value(T& value) {
    return read_exact(&value, sizeof(T));
}

template <typename T>
bool write_value(const T& value) {
    return write_exact(&value, sizeof(T));
}

std::string json_escape(const std::string& value) {
    std::string escaped;
    escaped.reserve(value.size() + 8);
    for (const char c : value) {
        switch (c) {
        case '\\': escaped += "\\\\"; break;
        case '"': escaped += "\\\""; break;
        default: escaped += c; break;
        }
    }
    return escaped;
}

}  // namespace

int main(int argc, char** argv) {
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
#endif
    if (argc < 3) {
        std::cerr << "usage: trans_ocr_host <responsePipeHandle> <modelDirectory> [threads]\n";
        return 2;
    }

#ifdef _WIN32
    g_response = reinterpret_cast<HANDLE>(std::strtoull(argv[1], nullptr, 10));
    if (g_response == nullptr || g_response == INVALID_HANDLE_VALUE) {
        std::cerr << "invalid response pipe handle: " << argv[1] << '\n';
        return 2;
    }
#else
    g_response = std::atoi(argv[1]);
    if (g_response < 0) {
        std::cerr << "invalid response pipe fd: " << argv[1] << '\n';
        return 2;
    }
#endif

    const int threads = argc > 3 ? std::atoi(argv[3]) : 8;
    const std::string config =
        std::string("{\"modelDirectory\":\"") + json_escape(argv[2]) +
        "\",\"threads\":" + std::to_string(threads) + "}";

    trans_ocr_engine engine = nullptr;
    char* error = nullptr;
    const auto createStatus = trans_ocr_create(config.c_str(), &engine, &error);
    if (createStatus != TRANS_OCR_OK || engine == nullptr) {
        std::cerr << (error == nullptr ? "engine init failed" : error) << '\n';
        trans_ocr_free_string(error);
        return 1;
    }
    trans_ocr_free_string(error);

    if (!write_value(int32_t{0})) { // ready
        return 1;
    }

    while (true) {
        int32_t width = 0;
        int32_t height = 0;
        int32_t stride = 0;
        uint64_t pixelBytes = 0;
        if (!read_value(width) || !read_value(height) || !read_value(stride) ||
            !read_value(pixelBytes)) {
            break; // Parent closed the pipe.
        }
        if (width <= 0 || height <= 0 || stride < width * 4 ||
            pixelBytes > (uint64_t{1} << 32)) {
            break; // Garbage framing; bail out instead of desynchronizing.
        }

        std::vector<uint8_t> pixels(static_cast<std::size_t>(pixelBytes));
        if (!read_exact(pixels.data(), pixels.size())) {
            break;
        }

        char* result = nullptr;
        error = nullptr;
        const auto status = trans_ocr_recognize_bgra(
            engine, pixels.data(), width, height, stride, &result, &error);
        const char* payload = status == TRANS_OCR_OK ? result : error;
        if (payload == nullptr) {
            payload = status == TRANS_OCR_OK ? "{}" : "OCR failed without a message";
        }
        const auto payloadLength = static_cast<uint64_t>(std::strlen(payload));
        if (!write_value(status) || !write_value(payloadLength) ||
            !write_exact(payload, payloadLength)) {
            break;
        }
        trans_ocr_free_string(result);
        trans_ocr_free_string(error);
    }

    trans_ocr_destroy(engine);
    return 0;
}
