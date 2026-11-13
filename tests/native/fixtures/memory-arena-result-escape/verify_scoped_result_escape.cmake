execute_process(
    COMMAND "${TEST_EXECUTABLE}"
    RESULT_VARIABLE result
    OUTPUT_VARIABLE output
    ERROR_VARIABLE error)

if("${result}" STREQUAL "0")
    message(FATAL_ERROR "scoped task result escaped into a durable task")
endif()
if(NOT "${error}" MATCHES "CX scoped reference cannot escape its arena")
    message(FATAL_ERROR
        "scoped result did not report the expected store rejection: ${output}${error}")
endif()
