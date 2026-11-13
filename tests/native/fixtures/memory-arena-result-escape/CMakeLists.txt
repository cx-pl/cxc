cmake_minimum_required(VERSION 3.31)
project(cx_memory_arena_result_escape C)
if(NOT DEFINED CX_GENERATED_DIR OR NOT DEFINED CXCORE_SOURCE_DIR)
    message(FATAL_ERROR "Set CX_GENERATED_DIR to compiler output and CXCORE_SOURCE_DIR to cxcore")
endif()
option(CX_STATIC_LINK "Use one statically linked runtime" ON)
set(CX_BUILD_STATIC ${CX_STATIC_LINK} CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
add_subdirectory(${CXCORE_SOURCE_DIR} cxcore)
add_executable(memoryArenaResultEscape
    main.c
    ${CX_GENERATED_DIR}/memoryArenaResultEscape.c)
target_include_directories(memoryArenaResultEscape PRIVATE ${CX_GENERATED_DIR})
target_link_libraries(memoryArenaResultEscape PRIVATE cxcore)
target_compile_features(memoryArenaResultEscape PRIVATE c_std_11)
enable_testing()
add_test(NAME memoryArenaResultEscapeRejectsScopedResult
    COMMAND ${CMAKE_COMMAND}
        -DTEST_EXECUTABLE=$<TARGET_FILE:memoryArenaResultEscape>
        -P ${CMAKE_CURRENT_LIST_DIR}/verify_scoped_result_escape.cmake)
