cmake_minimum_required(VERSION 3.31)
project(cx_memory_arena_dynamic_static_init C)
if(NOT DEFINED CX_GENERATED_DIR OR NOT DEFINED CXCORE_SOURCE_DIR)
    message(FATAL_ERROR "Set CX_GENERATED_DIR to compiler output and CXCORE_SOURCE_DIR to cxcore")
endif()
option(CX_STATIC_LINK "Use a statically linked runtime" ON)
set(CX_BUILD_STATIC ${CX_STATIC_LINK} CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
if(CX_STATIC_LINK AND CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(CMAKE_POSITION_INDEPENDENT_CODE ON)
endif()
add_subdirectory(${CXCORE_SOURCE_DIR} cxcore)

add_executable(memoryArenaDynamicStaticInit
    main.c
    ${CX_GENERATED_DIR}/memoryArenaDynamicStaticInit.c)
target_compile_definitions(memoryArenaDynamicStaticInit PRIVATE
    CX_MEMORYARENADYNAMICSTATICINIT_BUILD)
if(CX_STATIC_LINK)
    target_compile_definitions(memoryArenaDynamicStaticInit PRIVATE CX_STATIC_LINK)
else()
    target_compile_definitions(memoryArenaDynamicStaticInit PRIVATE CX_DYNAMIC_MODULE)
endif()
target_compile_features(memoryArenaDynamicStaticInit PRIVATE c_std_11)
if(MSVC)
    target_compile_options(memoryArenaDynamicStaticInit PRIVATE /utf-8 /experimental:c11atomics)
endif()
target_include_directories(memoryArenaDynamicStaticInit PRIVATE
    ${CX_GENERATED_DIR}
    ${CXCORE_SOURCE_DIR}/include
    ${CXCORE_SOURCE_DIR}/platform/common)
target_link_libraries(memoryArenaDynamicStaticInit PRIVATE cxcore)
if(NOT CX_STATIC_LINK)
    add_custom_command(TARGET memoryArenaDynamicStaticInit POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:cxcore>" "$<TARGET_FILE_DIR:memoryArenaDynamicStaticInit>")
endif()
