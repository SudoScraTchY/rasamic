cmake_minimum_required(VERSION 3.22)
project(rasamic_native LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

set(THIRD_PARTY "${CMAKE_CURRENT_SOURCE_DIR}/../third_party")
set(GEN_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")

if(MSVC)
  set(USE_ALLOCA 1)
  add_compile_options(/wd4244 /wd4267 /wd4305 /wd4100 /wd4456 /wd4701)
else()
  set(VAR_ARRAYS 1)
endif()

configure_file(cmake/rnnoise_config.h.in "${GEN_DIR}/rnnoise/config.h" COPYONLY)
configure_file(cmake/speexdsp_config.h.in "${GEN_DIR}/speexdsp/config.h" @ONLY)
configure_file(cmake/speexdsp_config_types.h.in "${GEN_DIR}/speexdsp/speex/speexdsp_config_types.h" COPYONLY)

# --- Opus (has native CMake support) ---
set(OPUS_BUILD_SHARED_LIBRARY OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_PKG_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_CMAKE_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
add_subdirectory("${THIRD_PARTY}/opus" "${CMAKE_BINARY_DIR}/opus")
if(NOT MSVC)
  # NEON asm (celt_tx_neon.S) addresses celt_tx_tab_* with ADRP; those symbols
  # must bind locally when linking a shared lib. Public API keeps default
  # visibility via explicit OPUS_EXPORT attributes.
  set_target_properties(opus PROPERTIES C_VISIBILITY_PRESET hidden)
endif()

# --- RNNoise (autotools upstream; compile library sources directly) ---
# src/rnnoise_data.c/h come from the official model tarball (rnnoise/download_model.sh).
add_library(rnnoise STATIC
  "${THIRD_PARTY}/rnnoise/src/denoise.c"
  "${THIRD_PARTY}/rnnoise/src/rnn.c"
  "${THIRD_PARTY}/rnnoise/src/nnet.c"
  "${THIRD_PARTY}/rnnoise/src/nnet_default.c"
  "${THIRD_PARTY}/rnnoise/src/rnnoise_data.c"
  "${THIRD_PARTY}/rnnoise/src/parse_lpcnet_weights.c"
  "${THIRD_PARTY}/rnnoise/src/rnnoise_tables.c"
  "${THIRD_PARTY}/rnnoise/src/pitch.c"
  "${THIRD_PARTY}/rnnoise/src/kiss_fft.c"
  "${THIRD_PARTY}/rnnoise/src/celt_lpc.c"
)
target_compile_definitions(rnnoise PRIVATE HAVE_CONFIG_H)
target_include_directories(rnnoise
  PUBLIC "${THIRD_PARTY}/rnnoise/include"
  PRIVATE "${THIRD_PARTY}/rnnoise/src" "${GEN_DIR}/rnnoise"
)
if(NOT MSVC)
  target_link_libraries(rnnoise PUBLIC m)
endif()

# --- SpeexDSP (autotools upstream; preprocess/AGC + resampler + jitter) ---
add_library(speexdsp STATIC
  "${THIRD_PARTY}/speexdsp/libspeexdsp/preprocess.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/resample.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/buffer.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/fftwrap.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/filterbank.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/jitter.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/kiss_fft.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/kiss_fftr.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/mdf.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/scal.c"
  "${THIRD_PARTY}/speexdsp/libspeexdsp/smallft.c"
)
target_compile_definitions(speexdsp PRIVATE HAVE_CONFIG_H FLOATING_POINT)
target_include_directories(speexdsp
  PUBLIC "${THIRD_PARTY}/speexdsp/include" "${GEN_DIR}/speexdsp" "${GEN_DIR}/speexdsp/speex"
  PRIVATE "${THIRD_PARTY}/speexdsp/libspeexdsp" "${GEN_DIR}/speexdsp"
)

# --- RasaMic native pipeline ---
if(ANDROID)
  set(OBOE_BUILD_TESTS OFF CACHE BOOL "" FORCE)
  add_subdirectory("${THIRD_PARTY}/oboe" oboe EXCLUDE_FROM_ALL)
  set(RM_CAPTURE_SRC src/capture_android.cpp)
  set(RM_MONITOR_SRC src/monitor.cpp)
elseif(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  enable_language(OBJCXX)
  set(RM_CAPTURE_SRC src/capture_ios.mm)
  # Client-only lib: speaker monitor is a server feature (miniaudio desktop backends).
  set(RM_MONITOR_SRC src/monitor_stub.cpp)
else()
  set(RM_CAPTURE_SRC src/capture_miniaudio.cpp)
  set(RM_MONITOR_SRC src/monitor.cpp)
endif()

add_library(rasamic_native SHARED src/pipeline.cpp ${RM_MONITOR_SRC} ${RM_CAPTURE_SRC})
target_include_directories(rasamic_native PRIVATE include "${THIRD_PARTY}/miniaudio")
target_link_libraries(rasamic_native PRIVATE opus rnnoise speexdsp)
if(ANDROID)
  target_link_libraries(rasamic_native PRIVATE oboe)
  # Android 16 requires 16 KB page-aligned ELF segments on arm64.
  target_link_options(rasamic_native PRIVATE "-Wl,-z,max-page-size=16384")
endif()
if(WIN32)
  target_link_libraries(rasamic_native PRIVATE ole32 winmm advapi32)
elseif(APPLE)
  target_link_libraries(rasamic_native PRIVATE "-framework AudioToolbox" "-framework CoreAudio" "-framework CoreFoundation")
  if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    target_link_libraries(rasamic_native PRIVATE "-framework AVFoundation" "-framework UIKit")
  endif()
elseif(ANDROID)
  target_link_libraries(rasamic_native PRIVATE log dl)
else()
  target_link_libraries(rasamic_native PRIVATE m dl pthread)
endif()
set_target_properties(rasamic_native PROPERTIES
  C_VISIBILITY_PRESET hidden
  CXX_VISIBILITY_PRESET hidden
)