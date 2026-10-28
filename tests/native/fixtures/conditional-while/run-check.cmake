execute_process(COMMAND "${CX_EXECUTABLE}" RESULT_VARIABLE result TIMEOUT 60)
if(NOT "${result}" STREQUAL "0")
    message(FATAL_ERROR "g4_conditional_while failed: process result ${result}")
endif()
