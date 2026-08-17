#include "trans_ocr.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <stdexcept>
#include <string>
#include <vector>

#if defined(TRANS_OCR_WITH_PADDLE)
#include <opencv2/imgproc.hpp>

#include "src/api/pipelines/ocr.h"
#include "src/pipelines/ocr/result.h"
#include "third_party/nlohmann/json.hpp"
#endif

namespace {

struct Engine final {
    std::string config_json;
#if defined(TRANS_OCR_WITH_PADDLE)
    std::unique_ptr<PaddleOCR> ocr;
    std::mutex inference_mutex;
#endif
};

#if defined(TRANS_OCR_WITH_PADDLE)
using json = nlohmann::json;

std::filesystem::path utf8_path(const std::string& value) {
    return std::filesystem::u8path(value);
}

std::string normalize_result_json(const OCRPipelineResult& raw, int width, int height) {
    json output;
    output["width"] = width;
    output["height"] = height;
    output["blocks"] = json::array();

    const auto& texts = raw.rec_texts;
    const auto& scores = raw.rec_scores;
    const auto& polygons = raw.rec_polys;
    const auto count = std::min({texts.size(), scores.size(), polygons.size()});

    // PaddleOCR detection order does not guarantee reading order: group blocks into
    // visual rows by vertical center proximity, then sort each row by horizontal center.
    struct BlockEntry {
        std::size_t index;
        double x0, y0, x1, y1;
        double center_x, center_y;
        double height;
    };
    std::vector<BlockEntry> blocks;
    blocks.reserve(count);
    for (std::size_t index = 0; index < count; ++index) {
        double x0 = std::numeric_limits<double>::max();
        double y0 = std::numeric_limits<double>::max();
        double x1 = std::numeric_limits<double>::lowest();
        double y1 = std::numeric_limits<double>::lowest();
        for (const auto& point : polygons.at(index)) {
            x0 = std::min(x0, static_cast<double>(point.x));
            y0 = std::min(y0, static_cast<double>(point.y));
            x1 = std::max(x1, static_cast<double>(point.x));
            y1 = std::max(y1, static_cast<double>(point.y));
        }
        blocks.push_back({index, x0, y0, x1, y1,
                          (x0 + x1) / 2.0, (y0 + y1) / 2.0, y1 - y0});
    }
    std::sort(blocks.begin(), blocks.end(), [](const BlockEntry& left, const BlockEntry& right) {
        return left.center_y < right.center_y;
    });
    std::vector<double> heights;
    heights.reserve(blocks.size());
    for (const auto& block : blocks) heights.push_back(block.height);
    std::sort(heights.begin(), heights.end());
    // Row tolerance is half the median block height; a block whose vertical center
    // deviates from the current row lead beyond the tolerance starts a new row.
    const double band = heights.empty() ? 8.0 : heights[heights.size() / 2] * 0.5;
    std::vector<std::vector<const BlockEntry*>> rows;
    for (const auto& block : blocks) {
        if (rows.empty() || std::abs(block.center_y - rows.back().front()->center_y) > band) {
            rows.emplace_back();
        }
        rows.back().push_back(&block);
    }
    for (auto& row : rows) {
        std::sort(row.begin(), row.end(), [](const BlockEntry* left, const BlockEntry* right) {
            return left->center_x < right->center_x;
        });
    }
    int order = 1;
    for (const auto& row : rows) {
        for (const auto* block : row) {
            json polygon = json::array();
            for (const auto& point : polygons.at(block->index)) {
                polygon.push_back({
                    static_cast<int>(std::round(point.x)),
                    static_cast<int>(std::round(point.y))
                });
            }
            output["blocks"].push_back({
                {"polygon", std::move(polygon)},
                {"text", texts.at(block->index)},
                {"confidence", scores.at(block->index)},
                {"reading_order", order++}
            });
        }
    }
    return output.dump();
}

#endif

char* duplicate_utf8(const std::string& value) {
    auto* result = static_cast<char*>(std::malloc(value.size() + 1));
    if (result == nullptr) {
        return nullptr;
    }

    std::memcpy(result, value.c_str(), value.size() + 1);
    return result;
}

void set_string(char** target, const std::string& value) {
    if (target != nullptr) {
        *target = duplicate_utf8(value);
    }
}

} // namespace

const char* TRANS_OCR_CALL trans_ocr_version(void) {
    return "0.1.0";
}

int TRANS_OCR_CALL trans_ocr_create(
    const char* config_json,
    trans_ocr_engine* out_engine,
    char** out_error_utf8) {
    if (out_engine == nullptr) {
        set_string(out_error_utf8, "out_engine must not be null");
        return TRANS_OCR_INVALID_ARGUMENT;
    }

    *out_engine = nullptr;
    if (out_error_utf8 != nullptr) {
        *out_error_utf8 = nullptr;
    }

    try {
        auto engine = std::make_unique<Engine>();
        engine->config_json = config_json == nullptr ? "{}" : config_json;
#if defined(TRANS_OCR_WITH_PADDLE)
        const auto config = json::parse(engine->config_json);
        const auto model_directory = utf8_path(config.at("modelDirectory").get<std::string>());
        const auto detection_directory = model_directory / "PP-OCRv5_mobile_det_infer";
        const auto recognition_directory = model_directory / "PP-OCRv5_mobile_rec_infer";
        if (!std::filesystem::is_directory(detection_directory) ||
            !std::filesystem::is_directory(recognition_directory)) {
            throw std::runtime_error("PP-OCRv5 mobile model directories were not found");
        }

        // The vendored pipeline prints its real error to stderr right before
        // calling exit(-1); capture stderr so failures stay diagnosable.
        static std::once_flag stderr_redirect_once;
        std::call_once(stderr_redirect_once, [] {
            if (const char* temp = std::getenv("TEMP")) {
                const auto log_path = std::filesystem::path(temp) / "transocr-native-stderr.log";
                FILE* redirected = nullptr;
                if (freopen_s(&redirected, log_path.string().c_str(), "a", stderr) != 0) {
                    std::fprintf(stderr, "trans_ocr: failed to redirect stderr to %s\n",
                                 log_path.string().c_str());
                }
            }
        });

        PaddleOCRParams params;
        params.device = "cpu";
        params.cpu_threads = config.value("threads", 8);
        params.enable_mkldnn = true;
        params.use_doc_orientation_classify = false;
        params.use_doc_unwarping = false;
        params.use_textline_orientation = false;
        params.text_detection_model_name = "PP-OCRv5_mobile_det";
        params.text_detection_model_dir = detection_directory.string();
        params.text_recognition_model_name = "PP-OCRv5_mobile_rec";
        params.text_recognition_model_dir = recognition_directory.string();
        params.text_recognition_batch_size = 12;
        params.text_det_limit_type = "max";
        params.text_det_limit_side_len = 1600;
        params.mkldnn_cache_capacity = 32;
        engine->ocr = std::make_unique<PaddleOCR>(params);
#endif
        *out_engine = engine.release();
        return TRANS_OCR_OK;
    } catch (const std::exception& exception) {
        set_string(out_error_utf8, exception.what());
        return TRANS_OCR_INITIALIZATION_FAILED;
    }
}

int TRANS_OCR_CALL trans_ocr_recognize_bgra(
    trans_ocr_engine engine,
    const uint8_t* pixels,
    int width,
    int height,
    int stride,
    char** out_result_json_utf8,
    char** out_error_utf8) {
    if (out_result_json_utf8 != nullptr) {
        *out_result_json_utf8 = nullptr;
    }
    if (out_error_utf8 != nullptr) {
        *out_error_utf8 = nullptr;
    }

    if (engine == nullptr || pixels == nullptr || width <= 0 || height <= 0 || stride < width * 4) {
        set_string(out_error_utf8, "invalid engine or BGRA image buffer");
        return TRANS_OCR_INVALID_ARGUMENT;
    }
    if (out_result_json_utf8 == nullptr) {
        set_string(out_error_utf8, "out_result_json_utf8 must not be null");
        return TRANS_OCR_INVALID_ARGUMENT;
    }

#if defined(TRANS_OCR_WITH_PADDLE)
    auto* typed_engine = static_cast<Engine*>(engine);
    try {
        std::lock_guard<std::mutex> lock(typed_engine->inference_mutex);
        cv::Mat bgra(height, width, CV_8UC4, const_cast<uint8_t*>(pixels), stride);
        cv::Mat bgr;
        cv::cvtColor(bgra, bgr, cv::COLOR_BGRA2BGR);
        std::vector<cv::Mat> inputs = {bgr};
        auto results = typed_engine->ocr->PredictMats(inputs);
        if (results.empty() || results.front() == nullptr) {
            throw std::runtime_error("PaddleOCR returned no page result");
        }
        const auto* ocr_result = dynamic_cast<const OCRResult*>(results.front().get());
        if (ocr_result == nullptr) {
            throw std::runtime_error("PaddleOCR returned an unexpected result type");
        }
        const auto normalized = normalize_result_json(
            ocr_result->PipelineResult(), width, height);
        set_string(out_result_json_utf8, normalized);
        return *out_result_json_utf8 == nullptr ? TRANS_OCR_RECOGNITION_FAILED : TRANS_OCR_OK;
    } catch (const std::exception& exception) {
        set_string(out_error_utf8, exception.what());
        return TRANS_OCR_RECOGNITION_FAILED;
    }
#else
    set_string(
        out_error_utf8,
        "PaddleOCR runtime is not staged yet; install the Windows CPU inference package and models");
    return TRANS_OCR_ENGINE_NOT_CONFIGURED;
#endif
}

void TRANS_OCR_CALL trans_ocr_free_string(char* value) {
    std::free(value);
}

void TRANS_OCR_CALL trans_ocr_destroy(trans_ocr_engine engine) {
    delete static_cast<Engine*>(engine);
}

