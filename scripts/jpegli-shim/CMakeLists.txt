# TrueToneCap jpegli shim 构建（由 scripts/build-cjpegli.py 驱动，勿单独使用）。
#
# 把 google/jpegli 源码树以子目录形式纳入，额外产出：
#   ttc_jpegli.dll   — 面向产品的 C-ABI shim（内部走 DecodeBytes → EncodeJpeg，
#                      与 cjpegli.exe 完全同一条上游代码路径）
#   ttc_selftest.exe — shim 与 cjpegli.exe 的逐字节对照器（采纳前的硬门禁，
#                      任何一例不一致即不得发布）
#
# 布局约定：本目录被复制到 jpegli 检出根下的 ttc-shim/（或与检出根同级放置，
# 如 C:\PLAN\jpegli-lib\shim ↔ C:\PLAN\jpegli-lib\jpegli），上一级即 jpegli 树根。
cmake_minimum_required(VERSION 3.16...3.27)
project(ttc_jpegli_shim CXX)

set(JPEGLI_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." CACHE PATH "google/jpegli checkout root")
if(NOT EXISTS "${JPEGLI_ROOT}/tools/cjpegli.cc")
  message(FATAL_ERROR "JPEGLI_ROOT (${JPEGLI_ROOT}) 不像 google/jpegli 检出根（tools/cjpegli.cc 缺失）")
endif()

# 与 scripts/build-cjpegli.py 的 cjpegli.exe 配方同源同参：
# 静态链接（BUILD_SHARED_LIBS=OFF ⇒ jpegli_cms 等一并静态化，单一 DLL 内闭合）、
# 关闭 sjpeg / libjpeg-API 兼容层（不引入 cjpegli.exe 配方之外的第 3 方依赖）。
set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(JPEGLI_ENABLE_TOOLS ON CACHE BOOL "" FORCE)     # cjpegli.exe 作对照基准
set(JPEGLI_ENABLE_BENCHMARK OFF CACHE BOOL "" FORCE)
set(JPEGLI_ENABLE_SJPEG OFF CACHE BOOL "" FORCE)
set(JPEGLI_ENABLE_JPEGLI_LIBJPEG OFF CACHE BOOL "" FORCE)
set(JPEGLI_ENABLE_DOXYGEN OFF CACHE BOOL "" FORCE)
set(JPEGLI_ENABLE_MANPAGES OFF CACHE BOOL "" FORCE)

add_subdirectory("${JPEGLI_ROOT}" jpegli_build)

add_library(ttc_jpegli SHARED ttc_shim.cc)
target_include_directories(ttc_jpegli PRIVATE "${JPEGLI_ROOT}")
# 链接集合对齐 tools/CMakeLists.txt 的工具 foreach（cjpegli 同款）：
# extras(EncodeJpeg) 需要核心 static、线程池 stub 与 CMS（JpegliGetDefaultCms）。
target_link_libraries(ttc_jpegli PRIVATE
  jpegli-static jpegli_extras-internal jpegli_threads jpegli_cms)

add_executable(ttc_selftest ttc_selftest.cc)
target_link_libraries(ttc_selftest PRIVATE ttc_jpegli)
set_target_properties(ttc_jpegli ttc_selftest PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/bin")
