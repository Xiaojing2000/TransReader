cmake_minimum_required(VERSION 3.24)

project(TransOcrNative VERSION 0.1.0 LANGUAGES CXX)

add_library(TransOcrNative SHARED
    include/trans_ocr.h
    src/trans_ocr.cpp
)

target_compile_features(TransOcrNative PRIVATE cxx_std_17)
target_compile_definitions(TransOcrNative PRIVATE TRANS_OCR_NATIVE_EXPORTS WIN32_LEAN_AND_MEAN NOMINMAX)
target_include_directories(TransOcrNative PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)

if(MSVC)
    target_compile_options(TransOcrNative PRIVATE /W4 /permissive- /utf-8)
endif()

set_target_properties(TransOcrNative PROPERTIES
    OUTPUT_NAME "TransOcrNative"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

install(TARGETS TransOcrNative RUNTIME DESTINATION .)

include(CTest)
if(BUILD_TESTING)
    add_executable(TransOcrNative.AbiSmoke tests/abi_smoke.cpp)
    target_compile_features(TransOcrNative.AbiSmoke PRIVATE cxx_std_17)
    target_link_libraries(TransOcrNative.AbiSmoke PRIVATE TransOcrNative)
    set_target_properties(TransOcrNative.AbiSmoke PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    )
    add_test(NAME TransOcrNative.AbiSmoke COMMAND TransOcrNative.AbiSmoke)
endif()
