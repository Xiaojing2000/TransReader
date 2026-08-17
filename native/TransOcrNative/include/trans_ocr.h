#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(TRANS_OCR_NATIVE_EXPORTS)
#    define TRANS_OCR_API __declspec(dllexport)
#  else
#    define TRANS_OCR_API __declspec(dllimport)
#  endif
#  define TRANS_OCR_CALL __cdecl
#else
#  define TRANS_OCR_API
#  define TRANS_OCR_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void* trans_ocr_engine;

enum trans_ocr_status {
    TRANS_OCR_OK = 0,
    TRANS_OCR_INVALID_ARGUMENT = 1,
    TRANS_OCR_INITIALIZATION_FAILED = 2,
    TRANS_OCR_RECOGNITION_FAILED = 3,
    TRANS_OCR_ENGINE_NOT_CONFIGURED = 4
};

// Returns a static UTF-8 version string. The caller must not free it.
TRANS_OCR_API const char* TRANS_OCR_CALL trans_ocr_version(void);

// Configuration is UTF-8 JSON. The returned error string, when present, must
// be released with trans_ocr_free_string.
TRANS_OCR_API int TRANS_OCR_CALL trans_ocr_create(
    const char* config_json,
    trans_ocr_engine* out_engine,
    char** out_error_utf8);

// pixels must point to a top-down BGRA8 image. Result JSON and error strings
// are UTF-8 and owned by the caller until trans_ocr_free_string is called.
TRANS_OCR_API int TRANS_OCR_CALL trans_ocr_recognize_bgra(
    trans_ocr_engine engine,
    const uint8_t* pixels,
    int width,
    int height,
    int stride,
    char** out_result_json_utf8,
    char** out_error_utf8);

TRANS_OCR_API void TRANS_OCR_CALL trans_ocr_free_string(char* value);
TRANS_OCR_API void TRANS_OCR_CALL trans_ocr_destroy(trans_ocr_engine engine);

#ifdef __cplusplus
}
#endif

