execute_process(COMMAND "${CX_EXECUTABLE}" RESULT_VARIABLE result TIMEOUT 45)
if(NOT "${result}" STREQUAL "0")
    message(FATAL_ERROR "reusable source generation failed: process result ${result}")
endif()
