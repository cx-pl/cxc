cmake_minimum_required(VERSION 3.31)
project(cx_coroutine_acceptance C)

if(NOT DEFINED CX_GENERATED_DIR OR NOT DEFINED CXCORE_SOURCE_DIR)
    message(FATAL_ERROR "Set CX_GENERATED_DIR and CXCORE_SOURCE_DIR")
endif()

option(CX_STATIC_LINK "Link the coroutine fixture to static cxcore" ON)
set(CX_BUILD_STATIC ${CX_STATIC_LINK} CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
add_subdirectory(${CXCORE_SOURCE_DIR} cxcore)

add_executable(coroutine_acceptance
    ${CX_GENERATED_DIR}/bench.c
    ${CMAKE_CURRENT_LIST_DIR}/main.c)
target_include_directories(coroutine_acceptance PRIVATE ${CX_GENERATED_DIR})
target_link_libraries(coroutine_acceptance PRIVATE cxcore)
target_compile_features(coroutine_acceptance PRIVATE c_std_11)
target_compile_definitions(coroutine_acceptance PRIVATE CX_BENCH_BUILD)

if(NOT CX_STATIC_LINK)
    target_compile_definitions(coroutine_acceptance PRIVATE CX_DYNAMIC_MODULE)
    if(WIN32)
        add_custom_command(TARGET coroutine_acceptance POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                $<TARGET_FILE:cxcore> $<TARGET_FILE_DIR:coroutine_acceptance>)
    endif()
endif()

enable_testing()
add_test(NAME coroutine_acceptance COMMAND $<TARGET_FILE:coroutine_acceptance>)
set_tests_properties(coroutine_acceptance PROPERTIES TIMEOUT 45)
