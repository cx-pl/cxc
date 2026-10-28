cmake_minimum_required(VERSION 3.31)
project(milestone18_g4_conditional_while C)
include(CTest)

set(CX_GENERATED_DIR "" CACHE PATH "Generated app project directory")
set(CXCORE_SOURCE_DIR "" CACHE PATH "cxcore source directory")
option(CX_STATIC_LINK "Link CX runtime statically" ON)

if(NOT EXISTS "${CX_GENERATED_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "CX_GENERATED_DIR must point to the emitted app project")
endif()
if(NOT EXISTS "${CXCORE_SOURCE_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "CXCORE_SOURCE_DIR must point to the cxcore source tree")
endif()

add_subdirectory("${CX_GENERATED_DIR}" generated)
target_compile_definitions(cxcore PRIVATE CXCORE_TESTING)
target_sources(app PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/allocation_probe.c")
add_test(NAME g4_conditional_while COMMAND "${CMAKE_COMMAND}"
    "-DCX_EXECUTABLE=$<TARGET_FILE:app>"
    -P "${CMAKE_CURRENT_SOURCE_DIR}/run-check.cmake")
set_tests_properties(g4_conditional_while PROPERTIES TIMEOUT 90)
