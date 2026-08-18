#include "trans_ocr.h"

#include <cassert>
#include <cstring>
#include <iostream>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>

namespace {
std::vector<uint8_t> make_test_page(int width, int height) {
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    HDC screen = GetDC(nullptr);
    HDC dc = CreateCompatibleDC(screen);
    HBITMAP bitmap = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    ReleaseDC(nullptr, screen);
    assert(dc != nullptr && bitmap != nullptr && bits != nullptr);

    const auto previousBitmap = SelectObject(dc, bitmap);
    RECT page{0, 0, width, height};
    FillRect(dc, &page, static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH)));
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, RGB(0, 0, 0));
    HFONT font = CreateFontW(
        82, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    const auto previousFont = SelectObject(dc, font);
    DrawTextW(dc, L"Hello PDF translation", -1, &page,
              DT_CENTER | DT_VCENTER | DT_SINGLELINE);

    std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
    memcpy(pixels.data(), bits, pixels.size());
    SelectObject(dc, previousFont);
    SelectObject(dc, previousBitmap);
    DeleteObject(font);
    DeleteObject(bitmap);
    DeleteDC(dc);
    return pixels;
}
}  // namespace
#endif

int main(int argc, char** argv) {
    assert(std::string(trans_ocr_version()) == "0.1.0");

    trans_ocr_engine engine = nullptr;
    char* error = nullptr;
    if (argc < 3) {
        std::cerr << "usage: TransOcrNative.AbiSmoke <models> <OCR.yaml>\n";
        return 2;
    }
    const std::string config = std::string("{\"modelDirectory\":\"") + argv[1] +
        "\",\"pipelineConfigPath\":\"" + argv[2] + "\",\"threads\":2}";
    const auto status = trans_ocr_create(config.c_str(), &engine, &error);
    assert(status == TRANS_OCR_OK);
    assert(engine != nullptr);
    trans_ocr_free_string(error);
    if (engine != nullptr) {
#ifdef _WIN32
        constexpr int width = 1200;
        constexpr int height = 260;
        const auto pixels = make_test_page(width, height);
        char* result = nullptr;
        error = nullptr;
        const auto recognizeStatus = trans_ocr_recognize_bgra(
            engine, pixels.data(), width, height, width * 4, &result, &error);
        if (recognizeStatus != TRANS_OCR_OK) {
            std::cerr << (error == nullptr ? "OCR failed" : error) << '\n';
        }
        assert(recognizeStatus == TRANS_OCR_OK);
        assert(result != nullptr);
        const std::string json(result);
        assert(json.find("Hello") != std::string::npos);
        std::cout << json << '\n';
        trans_ocr_free_string(result);
        trans_ocr_free_string(error);
#endif
        trans_ocr_destroy(engine);
    }
    return 0;
}
