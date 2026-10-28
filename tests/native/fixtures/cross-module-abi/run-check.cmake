# Keep a failing CX assertion's process exit code in CTest's output.
execute_process(COMMAND "${CX_EXECUTABLE}" RESULT_VARIABLE result TIMEOUT 60)
if(NOT "${result}" STREQUAL "0")
    message(FATAL_ERROR "cross_module_coroutine_abi failed: process result ${result}")
endif()
