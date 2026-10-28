cmake_minimum_required(VERSION 3.31)
project(cx_threading_conformance C)
if(NOT DEFINED CX_GENERATED_DIR OR NOT DEFINED CXCORE_SOURCE_DIR)
    message(FATAL_ERROR "Set CX_GENERATED_DIR to compiler output and CXCORE_SOURCE_DIR to cxcore")
endif()
option(CX_STATIC_LINK "Use one statically linked runtime" ON)
set(CX_BUILD_STATIC ${CX_STATIC_LINK} CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
add_subdirectory(${CXCORE_SOURCE_DIR} cxcore)
add_executable(threadingFixture
    ${CX_GENERATED_DIR}/threadingFixture.c
    ${CMAKE_CURRENT_LIST_DIR}/../threading-native.c)
target_include_directories(threadingFixture PRIVATE ${CX_GENERATED_DIR})
target_link_libraries(threadingFixture PRIVATE cxcore)
target_compile_features(threadingFixture PRIVATE c_std_11)
target_compile_definitions(threadingFixture PRIVATE CX_THREADINGFIXTURE_BUILD)
if(NOT CX_STATIC_LINK)
    target_compile_definitions(threadingFixture PRIVATE CX_DYNAMIC_MODULE)
    if(WIN32)
        add_custom_command(TARGET threadingFixture POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                $<TARGET_FILE:cxcore> $<TARGET_FILE_DIR:threadingFixture>)
    endif()
endif()
enable_testing()
add_test(NAME threadingFixture COMMAND ${CMAKE_COMMAND}
    -DTEST_EXECUTABLE=$<TARGET_FILE:threadingFixture>
    -P ${CMAKE_CURRENT_LIST_DIR}/../verify-threading-success.cmake)
set_tests_properties(threadingFixture PROPERTIES TIMEOUT 45)
foreach(mode IN ITEMS unobserved unreleased-mutex)
    if(mode STREQUAL "unobserved")
        set(expected "Uncaught exception thrown at")
    else()
        set(expected "cannot exit while owning a mutex")
    endif()
    add_test(NAME threading_${mode} COMMAND ${CMAKE_COMMAND}
        -DTEST_EXECUTABLE=$<TARGET_FILE:threadingFixture>
        -DTEST_MODE=${mode} -DEXPECTED_DIAGNOSTIC=${expected}
        -P ${CMAKE_CURRENT_LIST_DIR}/../verify-threading-failure.cmake)
endforeach()
