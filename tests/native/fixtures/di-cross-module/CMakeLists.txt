cmake_minimum_required(VERSION 3.31)
project(di_cross_module_acceptance C)
include(CTest)

set(CX_GENERATED_DIR "" CACHE PATH "Generated app project directory")
set(CXCORE_SOURCE_DIR "" CACHE PATH "cxcore source directory")
option(CX_STATIC_LINK "Link CX runtime and project references statically" ON)

if(NOT EXISTS "${CX_GENERATED_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "CX_GENERATED_DIR must point to the emitted app project")
endif()
if(NOT EXISTS "${CXCORE_SOURCE_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "CXCORE_SOURCE_DIR must point to the cxcore source tree")
endif()

add_subdirectory("${CX_GENERATED_DIR}" generated)
target_sources(app PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/app/main.c")
add_test(NAME di_cross_module_identity COMMAND "$<TARGET_FILE:app>")
